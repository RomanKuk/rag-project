import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { HistoryEntry, SourceReference } from '../models/chat.models';
import { AuthService } from './auth.service';

interface ServerChunk {
  type: 'token' | 'sources' | 'no_context' | 'done' | 'tool_call';
  token?: string;
  sources?: SourceReference[];
  message_id?: string;
  cost_usd?: number;
  cache_hit?: boolean;
  fallback_used?: boolean;
  usage?: { input_tokens: number; output_tokens: number; model: string };
  toolCall?: { tool: string; status: 'running' | 'done' };
  mode?: 'agent' | 'rag';
}

export interface StreamEvent {
  type: 'token' | 'sources' | 'no_context' | 'done' | 'tool_call';
  token?: string;
  sources?: SourceReference[];
  message_id?: string;
  cost_usd?: number;
  cache_hit?: boolean;
  usage?: { input_tokens: number; output_tokens: number; model: string };
  toolCall?: { tool: string; status: 'running' | 'done' };
  mode?: 'agent' | 'rag';
}

/** Thrown when /api/chat rejects the request before streaming (401, 429, ...). */
export class ChatStreamError extends Error {
  constructor(
    public readonly status: number,
    public readonly body: { error?: string; limit?: number; retryAfterUtc?: string } | null,
  ) {
    super(body?.error ?? `API error ${status}`);
  }
}

@Injectable({ providedIn: 'root' })
export class ChatService {
  private readonly apiUrl      = environment.apiUrl;
  private readonly authService = inject(AuthService);
  private readonly http        = inject(HttpClient);

  async sendFeedback(messageId: string | null, rating: 1 | -1, comment?: string): Promise<void> {
    const token = this.authService.token();
    const headers = token ? new HttpHeaders({ Authorization: `Bearer ${token}` }) : new HttpHeaders();
    await firstValueFrom(
      this.http.post(
        `${this.apiUrl}/api/chat/feedback`,
        { messageId, rating, comment },
        { headers }
      )
    );
  }

  async *streamAnswer(
    question: string,
    options: { sessionId?: string; history?: HistoryEntry[] } = {}
  ): AsyncGenerator<StreamEvent> {
    const headers: Record<string, string> = { 'Content-Type': 'application/json' };

    const token = this.authService.token();
    if (token) headers['Authorization'] = `Bearer ${token}`;

    const response = await fetch(`${this.apiUrl}/api/chat`, {
      method: 'POST',
      headers,
      body: JSON.stringify({
        question,
        sessionId: options.sessionId ?? null,
        history:   options.history   ?? [],
      }),
    });

    if (!response.ok) {
      let body = null;
      try { body = await response.json(); } catch { /* non-JSON error body */ }
      throw new ChatStreamError(response.status, body);
    }
    if (!response.body) throw new Error('No response body');

    const reader  = response.body.getReader();
    const decoder = new TextDecoder();
    let buffer    = '';

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
