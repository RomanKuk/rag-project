export interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
  sources: SourceReference[];
  isStreaming?: boolean;
}

export interface SourceReference {
  documentName: string;
  page: number;
  excerpt: string;
}
