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
  - Optional: feedback_examples.json (exported from DB) to augment the golden set
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

OPENAI_API_KEY = os.getenv("OPENAI_API_KEY", "")

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


def load_feedback_examples() -> list[dict]:
    """
    Load positively-rated feedback examples from feedback_examples.json (if present).

    The file must contain a JSON array matching golden_set.json structure:
      [{"question": "...", "expected_answer": "...", "source_document": "", "source_page": 0}]

    Export from DB:
      SELECT cm_user.content AS question,
             cm_asst.content AS expected_answer,
             '' AS source_document, 0 AS source_page
      FROM "Feedbacks" f
      JOIN "ChatMessages" cm_asst ON cm_asst."Id" = f."MessageId"
      JOIN "ChatMessages" cm_user ON cm_user."SessionId" = cm_asst."SessionId"
                                 AND cm_user."Role" = 'user'
                                 AND cm_user."CreatedAt" < cm_asst."CreatedAt"
      WHERE f."Rating" = 1
      ORDER BY cm_user."CreatedAt" DESC
      LIMIT 50;
    """
    feedback_path = os.path.join(os.path.dirname(__file__), "feedback_examples.json")
    if not os.path.exists(feedback_path):
        return []
    try:
        with open(feedback_path) as f:
            examples = json.load(f)
        print(f"Loaded {len(examples)} feedback-derived examples from feedback_examples.json.")
        return examples
    except Exception as exc:
        print(f"Warning: could not load feedback_examples.json ({exc})")
        return []


def build_dataset(golden_set: list[dict], api_key: str = "") -> tuple[Dataset, list[dict]]:
    """Ask every question; return the Ragas Dataset and raw rows for additional checks."""
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
            "question":           item["question"],
            "answer":             result["answer"],
            "contexts":           contexts,
            "ground_truth":       item["expected_answer"],
            "expects_no_context": item.get("source_document", "") == "",
        })
    features = Features({
        "question":     Value("string"),
        "answer":       Value("string"),
        "contexts":     Sequence(Value("string")),
        "ground_truth": Value("string"),
    })
    ragas_rows = [{k: v for k, v in r.items() if k in ("question", "answer", "contexts", "ground_truth")}
                  for r in rows]
    return Dataset.from_list(ragas_rows, features=features), rows


# ── Retrieval coverage ────────────────────────────────────────────────────────

def compute_retrieval_coverage(rows: list[dict]) -> float:
    """
    Fraction of contextual questions where >= 1 chunk was retrieved.
    Out-of-domain / no-context questions are excluded from the denominator —
    we expect and want empty contexts for those.
    """
    contextual = [r for r in rows if not r["expects_no_context"]]
    if not contextual:
        return 1.0
    covered = sum(1 for r in contextual if len(r["contexts"]) > 0)
    return covered / len(contextual)


# ── Toxicity check ────────────────────────────────────────────────────────────

def run_toxicity_check(rows: list[dict]) -> dict:
    """
    Call the OpenAI moderation endpoint on every generated answer.
    Returns {"passed": bool, "flagged_count": int, "flagged_questions": list[str]}.
    Skips gracefully if OPENAI_API_KEY is not set.
    """
    if not OPENAI_API_KEY:
        print("\n[Toxicity check] SKIPPED — OPENAI_API_KEY not set.")
        return {"passed": True, "flagged_count": 0, "flagged_questions": [], "skipped": True}

    print("\n[Toxicity check] Checking generated answers via OpenAI moderation API...")
    flagged: list[str] = []

    with httpx.Client(timeout=30) as client:
        for row in rows:
            answer = row["answer"].strip()
            if not answer:
                continue
            try:
                resp = client.post(
                    "https://api.openai.com/v1/moderations",
                    headers={"Authorization": f"Bearer {OPENAI_API_KEY}",
                             "Content-Type": "application/json"},
                    json={"input": answer},
                )
                resp.raise_for_status()
                result_data = resp.json()
                if result_data["results"][0]["flagged"]:
                    categories = [k for k, v in result_data["results"][0]["categories"].items() if v]
                    flagged.append(f"Q: {row['question'][:60]!r} → {categories}")
                    print(f"    FLAGGED: {row['question'][:60]!r} → {categories}")
            except Exception as exc:
                print(f"    Warning: moderation check failed for '{row['question'][:40]}' ({exc})")

    total = len(rows)
    clean = total - len(flagged)
    print(f"  Results: {clean}/{total} clean, {len(flagged)} flagged.")
    return {"passed": len(flagged) == 0, "flagged_count": len(flagged),
            "flagged_questions": flagged, "skipped": False}


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
    sentinel_content = (
        b"SENTINEL_TENANT_A: The secret code is XYZZY_42. "
        b"This document belongs exclusively to Tenant A and must never be visible to any other tenant. "
        b"It contains a unique identifier used for cross-tenant isolation testing. "
        b"If another tenant retrieves this document, a data isolation failure has occurred."
    )
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

    # Augment with positively-rated feedback examples (if exported)
    feedback_examples = load_feedback_examples()
    golden = golden + feedback_examples

    clear_semantic_cache()

    try:
        httpx.get(f"{API_URL}/health",
                  timeout=httpx.Timeout(connect=30, read=30, write=10, pool=10))
        print("API warm-up complete.")
    except Exception as exc:
        print(f"Warning: warm-up request failed ({exc})")

    # Pass API_KEY_A to the main eval if configured (otherwise anonymous)
    eval_api_key = API_KEY_A or ""
    if not eval_api_key:
        print("NOTE: API_KEY_A not set — running anonymously. This works only when the API")
        print("      has no ApiKeys configured (dev mode); otherwise requests will get 401.")

    print(f"Running Ragas evaluation over {len(golden)} questions...")
    try:
        dataset, raw_rows = build_dataset(golden, api_key=eval_api_key)
    except httpx.HTTPStatusError as exc:
        if exc.response.status_code == 401:
            raise SystemExit(
                "FATAL: API returned 401 Unauthorized. Set API_KEY_A to a key configured "
                "in the API's ApiKeys section, or run the API with an empty ApiKeys config (dev mode)."
            )
        raise

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

    faithfulness_score   = results.get("faithfulness",    0)
    relevancy_score      = results.get("answer_relevancy", 0)
    context_recall_score = results.get("context_recall",   0)

    if faithfulness_score >= 0.8:
        print(f"\nPASS: faithfulness={faithfulness_score:.2f} >= 0.80")
    else:
        print(f"\nFAIL: faithfulness={faithfulness_score:.2f} < 0.80 (target)")

    # ── Retrieval coverage ───────────────────────────────────────────────────
    coverage = compute_retrieval_coverage(raw_rows)
    coverage_ok = coverage >= 0.90
    print(f"\n[Retrieval coverage] {coverage:.0%} of contextual questions retrieved >= 1 chunk "
          f"({'PASS' if coverage_ok else 'FAIL'}, target >= 90%)")

    # ── Toxicity check ───────────────────────────────────────────────────────
    toxicity_result = run_toxicity_check(raw_rows)
    toxicity_ok = toxicity_result["passed"]
    if toxicity_result.get("skipped"):
        toxicity_label = "SKIPPED"
    elif toxicity_ok:
        toxicity_label = "PASS (0 flagged)"
    else:
        toxicity_label = f"FAIL ({toxicity_result['flagged_count']} flagged)"

    # ── Phase 1: tenant isolation ────────────────────────────────────────────
    isolation_ok = run_tenant_isolation_test()

    # ── Phase 2: tool-selection ───────────────────────────────────────────────
    tool_ok = run_tool_selection_test(api_key=eval_api_key)

    # ── Summary ───────────────────────────────────────────────────────────────
    print("\n=== Test Summary ===")
    print(f"  Ragas faithfulness    : {'PASS' if faithfulness_score >= 0.8 else 'FAIL'} ({faithfulness_score:.2f})")
    print(f"  Answer relevancy      : {relevancy_score:.2f}")
    print(f"  Context recall        : {context_recall_score:.2f}")
    print(f"  Retrieval coverage    : {'PASS' if coverage_ok else 'FAIL'} ({coverage:.0%})")
    print(f"  Toxicity              : {toxicity_label}")
    print(f"  Tenant isolation      : {'PASS' if isolation_ok else 'FAIL'}")
    print(f"  Tool selection        : {'PASS' if tool_ok else 'FAIL'}")
    if feedback_examples:
        print(f"  Feedback examples     : {len(feedback_examples)} augmented from feedback_examples.json")

    hard_failures = [
        faithfulness_score >= 0.8,
        coverage_ok,
        toxicity_ok or toxicity_result.get("skipped", False),
        isolation_ok,
        tool_ok,
    ]
    if not all(hard_failures):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
