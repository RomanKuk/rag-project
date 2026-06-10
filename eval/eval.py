"""
Ragas evaluation script for the Document Q&A RAG system.

Usage:
  pip install -r requirements.txt
  python eval.py

Prerequisites:
  - Backend API running at http://localhost:5000
  - Documents from golden_set.json already ingested
  - OPENAI_API_KEY environment variable set (Ragas uses it for judge-LLM scoring)
  - Optional: API_KEY_A / API_KEY_B env vars for tenant-isolation test
"""

import json
import os
import httpx
from datasets import Dataset, Features, Value, Sequence
from ragas import evaluate
from ragas.metrics import faithfulness, answer_relevancy, context_precision, context_recall, answer_correctness

# Disable langchain's LLM response cache — without this, Ragas replays
# judge-LLM answers from previous runs and scores are frozen.
try:
    from langchain_core.globals import set_llm_cache
    set_llm_cache(None)
except ImportError:
    try:
        import langchain
        langchain.llm_cache = None  # type: ignore[attr-defined]
    except Exception:
        pass

API_URL       = os.getenv("API_URL",     "http://localhost:5000")
QDRANT_URL    = os.getenv("QDRANT_URL",  "http://localhost:6333")
CHAT_ENDPOINT = f"{API_URL}/api/chat"
UPLOAD_ENDPOINT = f"{API_URL}/api/documents/upload"

# Optional API keys for tenant-isolation test
API_KEY_A = os.getenv("API_KEY_A", "")
API_KEY_B = os.getenv("API_KEY_B", "")


def clear_semantic_cache() -> None:
    """Delete the cache_entries Qdrant collection so eval hits real retrieval, not cached answers."""
    try:
        r = httpx.delete(f"{QDRANT_URL}/collections/cache_entries", timeout=10)
        if r.status_code in (200, 404):
            print("Semantic cache cleared.")
        else:
            print(f"Warning: cache clear returned HTTP {r.status_code}")
    except Exception as exc:
        print(f"Warning: could not clear cache ({exc}) — results may reflect cached answers.")


def ask(question: str, api_key: str = "", agent: bool = False) -> dict:
    """Call the chat endpoint and collect the full streamed answer."""
    full_answer = ""
    sources: list[dict] = []
    tool_calls: list[dict] = []

    headers = {"Content-Type": "application/json"}
    if api_key:
        headers["X-API-Key"] = api_key

    with httpx.stream(
        "POST", CHAT_ENDPOINT,
        json={"question": question, "agent": agent},
        headers=headers,
        timeout=httpx.Timeout(connect=30, read=120, write=30, pool=30),
    ) as response:
        response.raise_for_status()
        buffer = ""
        for chunk in response.iter_text():
            buffer += chunk
            while "\n\n" in buffer:
                event, buffer = buffer.split("\n\n", 1)
                for line in event.splitlines():
                    if not line.startswith("data: "):
                        continue
                    data = line[6:].strip()
                    if data == "[DONE]":
                        break
                    try:
                        parsed = json.loads(data)
                        chunk_type = parsed.get("type", "")
                        if chunk_type == "token":
                            full_answer += parsed.get("token", "")
                        elif chunk_type == "sources":
                            sources = parsed.get("sources", [])
                        elif chunk_type == "no_context":
                            full_answer = "I cannot find this information in the available documents."
                        elif chunk_type == "tool_call":
                            tool_calls.append({"tool": parsed.get("toolCall", {}).get("tool", ""),
                                               "status": parsed.get("toolCall", {}).get("status", "")})
                    except json.JSONDecodeError:
                        pass

    return {"answer": full_answer, "sources": sources, "tool_calls": tool_calls}


def build_dataset(golden_set: list[dict], api_key: str = "") -> Dataset:
    rows = []
    for item in golden_set:
        print(f"  Asking: {item['question'][:60]}...")
        result = ask(item["question"], api_key=api_key)
        contexts = [
            f"[{s.get('documentName', '')}, page {s.get('page', 0)}]\n{s.get('excerpt', '')}"
            for s in result.get("sources", [])
        ]
        print(f"    Answer ({len(result['answer'])} chars): {result['answer'][:120]!r}")
        print(f"    Contexts: {len(contexts)} chunks, sizes={[len(c) for c in contexts]}")
        rows.append({
            "question":     item["question"],
            "answer":       result["answer"],
            "contexts":     contexts,
            "ground_truth": item["expected_answer"],
        })
    features = Features({
        "question":     Value("string"),
        "answer":       Value("string"),
        "contexts":     Sequence(Value("string")),
        "ground_truth": Value("string"),
    })
    return Dataset.from_list(rows, features=features)


# ── Tenant isolation test ────────────────────────────────────────────────────

def run_tenant_isolation_test() -> bool:
    """
    Ingest a document as tenant A, then query as tenant B.
    Passes if tenant B retrieves zero results.

    Requires API_KEY_A and API_KEY_B env vars. Skips if not configured.
    """
    if not API_KEY_A or not API_KEY_B:
        print("\n[Tenant isolation test] SKIPPED — set API_KEY_A and API_KEY_B to enable.")
        return True

    print("\n[Tenant isolation test] Uploading sentinel document as tenant A...")
    sentinel_content = b"SENTINEL_TENANT_A: The secret code is XYZZY_42."
    files = {"file": ("sentinel.txt", sentinel_content, "text/plain")}
    headers_a = {"X-API-Key": API_KEY_A}
    try:
        r = httpx.post(UPLOAD_ENDPOINT, files=files, headers=headers_a, timeout=60)
        r.raise_for_status()
        print(f"    Uploaded: {r.json()}")
    except Exception as exc:
        print(f"    FAIL: upload failed ({exc})")
        return False

    print("[Tenant isolation test] Querying as tenant B...")
    result = ask("What is the secret code XYZZY_42?", api_key=API_KEY_B)
    answer_lower = result["answer"].lower()
    sources_count = len(result["sources"])

    # Tenant B must NOT see the sentinel document
    leaked = "xyzzy" in answer_lower or sources_count > 0
    if leaked:
        print(f"    FAIL: tenant B retrieved tenant A's data! answer={result['answer'][:100]!r}, sources={sources_count}")
        return False
    else:
        print(f"    PASS: tenant B received no context (sources={sources_count}, answer={result['answer'][:80]!r})")
        return True


# ── Tool-selection test ──────────────────────────────────────────────────────

TOOL_SELECTION_CASES = [
    {
        "question": "What are the main topics covered in the RAG guide?",
        "expected_tool": "search_documents",
        "description": "factual search",
    },
    {
        "question": "Summarize the RAG guide document",
        "expected_tool": "summarize",
        "description": "explicit summarize request",
    },
]

def run_tool_selection_test(api_key: str = "") -> bool:
    """Ask agent-mode questions and assert the expected tool was called."""
    print("\n[Tool selection test] Running agent-mode tool routing checks...")
    all_passed = True
    for case in TOOL_SELECTION_CASES:
        result = ask(case["question"], api_key=api_key, agent=True)
        called_tools = [tc["tool"] for tc in result.get("tool_calls", [])]
        passed = case["expected_tool"] in called_tools
        status = "PASS" if passed else "FAIL"
        print(f"    [{status}] {case['description']}: expected={case['expected_tool']!r}, called={called_tools}")
        if not passed:
            all_passed = False
    return all_passed


# ── Main ─────────────────────────────────────────────────────────────────────

def main() -> None:
    golden_path = os.path.join(os.path.dirname(__file__), "golden_set.json")
    with open(golden_path) as f:
        golden = json.load(f)

    clear_semantic_cache()

    try:
        httpx.get(f"{API_URL}/health",
                  timeout=httpx.Timeout(connect=30, read=30, write=10, pool=10))
        print("API warm-up complete.")
    except Exception as exc:
        print(f"Warning: warm-up request failed ({exc})")

    # Pass API_KEY_A to the main eval if configured (otherwise anonymous)
    eval_api_key = API_KEY_A or ""

    print(f"Running Ragas evaluation over {len(golden)} questions...")
    dataset = build_dataset(golden, api_key=eval_api_key)

    results = evaluate(
        dataset,
        metrics=[
            faithfulness,
            answer_relevancy,
            context_precision,
            context_recall,
            answer_correctness,
        ],
    )

    print("\n=== Ragas Evaluation Results ===")
    print(results)

    faithfulness_score = results.get("faithfulness", 0)
    if faithfulness_score >= 0.8:
        print(f"\nPASS: faithfulness={faithfulness_score:.2f} >= 0.80")
    else:
        print(f"\nFAIL: faithfulness={faithfulness_score:.2f} < 0.80 (target)")

    # ── Phase 1: tenant isolation ────────────────────────────────────────────
    isolation_ok = run_tenant_isolation_test()

    # ── Phase 2: tool-selection ───────────────────────────────────────────────
    tool_ok = run_tool_selection_test(api_key=eval_api_key)

    # ── Summary ───────────────────────────────────────────────────────────────
    print("\n=== Test Summary ===")
    print(f"  Ragas faithfulness : {'PASS' if faithfulness_score >= 0.8 else 'FAIL'} ({faithfulness_score:.2f})")
    print(f"  Tenant isolation   : {'PASS' if isolation_ok else 'FAIL'}")
    print(f"  Tool selection     : {'PASS' if tool_ok else 'FAIL'}")

    if not (faithfulness_score >= 0.8 and isolation_ok and tool_ok):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
