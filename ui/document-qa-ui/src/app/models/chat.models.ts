export interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
  sources: SourceReference[];
  isStreaming?: boolean;
  costUsd?: number;
  cacheHit?: boolean;
  model?: string;
  activeToolCall?: string;
}

export interface SourceReference {
  documentName: string;
  page: number;
  excerpt: string;
}

export interface HistoryEntry {
  role: 'user' | 'assistant';
  content: string;
}
