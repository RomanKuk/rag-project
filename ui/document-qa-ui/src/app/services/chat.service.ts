import { Injectable, inject } from '@angular/core';
import { environment } from '../../environments/environment';
import { HistoryEntry, SourceReference } from '../models/chat.models';
import { ApiKeyService } from './api-key.service';
import { AuthService } from './auth.service';

interface ServerChunk {
  type: 'token' | 'sources' | 'no_context' | 'done' | 'tool_call';
  token?: string;
  sources?: SourceReference[];
  cost_usd?: number;
  cache_hit?: boolean;
  fallback_used?: boolean;
  usage?: { input_tokens: number; output_tokens: number; model: string };
  toolCall?: { tool: string; status: 'running' | 'done' };
}

export interface StreamEvent {
  type: 'token' | 'sources' | 'no_context' | 'done' | 'tool_call';
  token?: string;
  sources?: SourceReference[];
  cost_usd?: number;
  cache_hit?: boolean;
  usage?: { input_tokens: number; output_tokens: number; model: string };
  toolCall?: { tool: string; status: 'running' | 'done' };
}

@Injectable({ providedIn: 'root' })
export class ChatService {
  private readonly apiUrl        = environment.apiUrl;
  private readonly apiKeyService = inject(ApiKeyService);
  private readonly authService   = inject(AuthService);

  async *streamAnswer(
    question: string,
    options: { agent?: boolean; private?: boolean; history?: HistoryEntry[] } = {}
  ): AsyncGenerator<StreamEvent> {
    const headers: Record<string, string> = { 'Content-Type': 'application/json' };

    // JWT takes priority; fall back to X-API-Key for programmatic / eval use
    const token = this.authService.token();
    if (token) {
      headers['Authorization'] = `Bearer ${token}`;
    } else {
      const key = this.apiKeyService.get();
      if (key) headers['X-API-Key'] = key;
    }

    const response = await fetch(`${this.apiUrl}/api/chat`, {
      method: 'POST',
      headers,
      body: JSON.stringify({
        question,
        agent:   options.agent   ?? false,
        private: options.private ?? false,
        history: options.history ?? [],
      }),
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
