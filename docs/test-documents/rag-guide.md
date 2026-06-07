# Comprehensive Guide to Teaching and Testing RAG Systems

This document serves as a structured reference guide for setting up educational workshops, coding bootcamps, or testing pipelines for Retrieval-Augmented Generation (RAG).

---

## 1. Curated Document Sets for RAG Testing

When introducing students to RAG, it is best to start with accessible, pre-curated datasets rather than massive corporate dumps. Use these resources to experiment with chunking and retrieval tuning:

- **Kaggle Sample RAG Knowledge Item Dataset**
  - Purpose: A dedicated, small-scale dataset specifically built for RAG beginners.
  - Key Use Case: Ideal for experimenting with basic chunking strategies, token splitting, and vector database embeddings.

- **Hugging Face Advanced RAG Cookbook**
  - Purpose: A comprehensive guide that includes ready-to-use pipelines and sample open-source documentation.
  - Key Use Case: Perfect for testing advanced retrieval techniques like bi-encoders, cross-encoders, and reranking mechanisms.

- **Microsoft Learn RAG Preparation Guide**
  - Purpose: Architecture-focused guide with representative corporate and technical content templates.
  - Key Use Case: Teaches students how to clean, structure, and prepare production-grade data to ensure use-case completeness.

---

## 2. Core Teaching Vectors & Test Scenarios

To truly teach students how RAG works (and where it fails), your test source files must contain specific logical traps. Ensure your markdown or text data includes the following four scenarios:

### Overlapping Contexts (Entity Ambiguity)

- Setup: Include multiple documents or paragraphs that mention the exact same entity but in different contexts.
- Example: "Project Apollo Phase 1" using AWS in Doc A, and "Project Apollo Phase 2" using GCP in Doc B.
- Lesson: Teaches students about K-value retrieval limitations, vector similarity noise, and the importance of chunk overlap and context scoring.

### Contradictory Timelines (Data Obsolescence)

- Setup: Place an outdated organizational policy and a brand-new policy within the same knowledge base.
- Example: "Remote work policy 2024 (4 days in office)" vs "Remote work policy 2026 (Remote-first)".
- Lesson: Demonstrates why LLMs hallucinate old data if top-k similarity blindly pulls the wrong chunk. Teaches metadata filtering and time-based vector weight decay.

### Multi-Hop Reasoning (Information Fragmentation)

- Setup: Split a single fact across two completely separate paragraphs or documents.
- Example: Paragraph 1 states "John works in Office A." Paragraph 2 states "Office A is located in Lviv."
- Question to test: "Which city does John work in?"
- Lesson: Shows the limitation of simple, single-vector lookups. Teaches advanced concepts like query rewriting, sub-queries, and agentic RAG routing.

### Out-of-Domain Traps (Guardrails & Hallucinations)

- Setup: Inject completely irrelevant, random data paragraphs into an otherwise professional corporate knowledge base.
- Example: Inserting a traditional recipe for Ukrainian borsch inside a software engineering handbook.
- Question to test: "What is the financial budget allocated for making borsch?"
- Lesson: Evaluates system prompts and guardrails. Students learn how to force the LLM to say "I cannot find this information in the context" instead of hallucinating a fake corporate budget.

---

## 3. Evaluation Frameworks for RAG Systems

Building the pipeline is only half the battle. Students must learn how to statistically evaluate both the retriever (did we get the right context?) and the generator (did the LLM answer accurately based on that context?).

### Ragas Framework (Retrieval and Generation Evaluation)

- Focus: Industry-standard automated evaluation utilizing LLM-as-a-judge metrics.
- Key Metrics:
  - Faithfulness: measures groundedness — whether every claim in the answer is supported by the retrieved context.
  - Answer Relevancy: measures whether the answer actually addresses the user prompt.
  - Context Recall: measures whether the retriever captured all necessary source data to answer the question.

### LangChain Evaluation Guide & LangSmith

- Focus: Production tracing and systematic dataset testing.
- Key Metrics: Great for benchmarking retrieval precision, tracking token latency, and evaluating complex, multi-agent RAG workflows over time.
- LangSmith provides a UI for inspecting every retrieval call, prompt sent to the LLM, and the final generated answer, making it straightforward to diagnose retrieval failures.
