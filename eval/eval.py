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
from datasets import Dataset
from ragas import evaluate
from ragas.metrics import faithfulness, answer_relevancy, context_precision

API_URL = os.getenv("API_URL", "http://localhost:5000")
CHAT_ENDPOINT = f"{API_URL}/api/chat"


def ask(question: str) -> dict:
    """Call the chat endpoint and collect the full streamed answer."""
    full_answer = ""
    sources: list[dict] = []

    with httpx.stream("POST", CHAT_ENDPOINT,
                      json={"question": question},
                      timeout=60) as response:
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
                        if "token" in parsed:
                            full_answer += parsed["token"]
                        if "sources" in parsed:
                            sources = parsed["sources"]
                    except json.JSONDecodeError:
                        pass

    return {"answer": full_answer, "sources": sources}


def build_dataset(golden_set: list[dict]) -> Dataset:
    rows = []
    for item in golden_set:
        print(f"  Asking: {item['question'][:60]}...")
        result = ask(item["question"])
        rows.append({
            "question": item["question"],
            "answer": result["answer"],
            "contexts": [s.get("excerpt", "") for s in result.get("sources", [])],
            "ground_truth": item["expected_answer"],
        })
    return Dataset.from_list(rows)


def main() -> None:
    golden_path = os.path.join(os.path.dirname(__file__), "golden_set.json")
    with open(golden_path) as f:
        golden = json.load(f)

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
