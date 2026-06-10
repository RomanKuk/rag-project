"""
Ragas evaluation script for the Document Q&A RAG system.

Usage:
  pip install -r requirements.txt
  python eval.py

Prerequisites:
  - Backend API running at http://localhost:5000
  - Documents from golden_set.json already ingested
  - OPENAI_API_KEY environment variable set (Ragas uses it for judge-LLM scoring)
"""

import json
import os
import httpx
from datasets import Dataset, Features, Value, Sequence
from ragas import evaluate
from ragas.metrics import faithfulness, answer_relevancy, context_precision

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

API_URL     = os.getenv("API_URL",     "http://localhost:5000")
QDRANT_URL  = os.getenv("QDRANT_URL",  "http://localhost:6333")
CHAT_ENDPOINT = f"{API_URL}/api/chat"


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


def ask(question: str) -> dict:
    """Call the chat endpoint and collect the full streamed answer."""
    full_answer = ""
    sources: list[dict] = []

    with httpx.stream("POST", CHAT_ENDPOINT,
                      json={"question": question},
                      timeout=httpx.Timeout(connect=30, read=120, write=30, pool=30)) as response:
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
                    except json.JSONDecodeError:
                        pass

    return {"answer": full_answer, "sources": sources}


def build_dataset(golden_set: list[dict]) -> Dataset:
    rows = []
    for item in golden_set:
        print(f"  Asking: {item['question'][:60]}...")
        result = ask(item["question"])
        # Prepend "[DocumentName, page N]" to each excerpt so Ragas can verify the
        # inline citations the LLM adds (e.g. "[tech-reference.pdf, page 1]").
        contexts = [
            f"[{s.get('documentName', '')}, page {s.get('page', 0)}]\n{s.get('excerpt', '')}"
            for s in result.get("sources", [])
        ]
        print(f"    Answer ({len(result['answer'])} chars): {result['answer'][:120]!r}")
        print(f"    Contexts: {len(contexts)} chunks, sizes={[len(c) for c in contexts]}")
        rows.append({
            "question": item["question"],
            "answer": result["answer"],
            "contexts": contexts,
            "ground_truth": item["expected_answer"],
        })
    features = Features({
        "question":     Value("string"),
        "answer":       Value("string"),
        "contexts":     Sequence(Value("string")),
        "ground_truth": Value("string"),
    })
    return Dataset.from_list(rows, features=features)


def main() -> None:
    golden_path = os.path.join(os.path.dirname(__file__), "golden_set.json")
    with open(golden_path) as f:
        golden = json.load(f)

    # Clear cache so each question goes through real retrieval — cache hits return
    # empty contexts, which Ragas scores as faithfulness=0 (a misleading false negative).
    clear_semantic_cache()

    # Warm the API (first request after a restart embeds & initialises connections).
    try:
        httpx.get(f"{API_URL}/health",
                  timeout=httpx.Timeout(connect=30, read=30, write=10, pool=10))
        print("API warm-up complete.")
    except Exception as exc:
        print(f"Warning: warm-up request failed ({exc})")

    print(f"Running evaluation over {len(golden)} questions...")
    dataset = build_dataset(golden)

    results = evaluate(
        dataset,
        metrics=[faithfulness, answer_relevancy, context_precision],
    )

    print("\n=== Ragas Evaluation Results ===")
    print(results)

    # Target: faithfulness >= 0.8
    score = results.get("faithfulness", 0)
    if score >= 0.8:
        print(f"\nPASS: faithfulness={score:.2f} >= 0.80")
    else:
        print(f"\nFAIL: faithfulness={score:.2f} < 0.80 (target)")


if __name__ == "__main__":
    main()
