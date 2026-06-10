import { Injectable } from '@angular/core';
import { environment } from '../../environments/environment';
import { SourceReference } from '../models/chat.models';

interface ServerChunk {
  type: 'token' | 'sources' | 'no_context' | 'done';
  token?: string;
  sources?: SourceReference[];
  cost_usd?: number;
  cache_hit?: boolean;
  fallback_used?: boolean;
  usage?: { input_tokens: number; output_tokens: number; model: string };
}

export interface StreamEvent {
  type: 'token' | 'sources' | 'no_context' | 'done';
  token?: string;
  sources?: SourceReference[];
  cost_usd?: number;
  cache_hit?: boolean;
  usage?: { input_tokens: number; output_tokens: number; model: string };
}

@Injectable({ providedIn: 'root' })
export class ChatService {
  private readonly apiUrl = environment.apiUrl;

  // fetch + ReadableStream for POST-based SSE (EventSource only supports GET)
  async *streamAnswer(question: string): AsyncGenerator<StreamEvent> {
    const response = await fetch(`${this.apiUrl}/api/chat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ question })
    });

    if (!response.ok) throw new Error(`API error ${response.status}`);
    if (!response.body) throw new Error('No response body');

    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer = '';

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split('\n');
      buffer = lines.pop() ?? '';

      for (const line of lines) {
        if (!line.startsWith('data: ')) continue;
        const data = line.slice(6).trim();
        if (data === '[DONE]') return;

        try {
          const chunk = JSON.parse(data) as ServerChunk;
          yield chunk;
        } catch {
          // skip malformed chunk
        }
      }
    }
  }
}
