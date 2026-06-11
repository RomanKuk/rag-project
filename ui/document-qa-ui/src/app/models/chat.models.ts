export interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
  sources: SourceReference[];
  isStreaming?: boolean;
  costUsd?: number;
  cacheHit?: boolean;
  model?: string;
  activeToolCall?: string;
  feedback?: 1 | -1; // user thumbs up/down
  messageId?: string; // persisted message ID for feedback
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
