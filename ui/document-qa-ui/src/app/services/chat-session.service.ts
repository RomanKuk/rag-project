import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuthService } from './auth.service';

export interface ChatSessionSummary {
  id: string;
  title: string;
  includeSharedDocs: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface ChatSessionDetail extends ChatSessionSummary {
  messages: ChatMessageDto[];
}

export interface ChatMessageDto {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  sourcesJson?: string;
  inputTokens: number;
  outputTokens: number;
  costUsd: number;
  createdAt: string;
}

@Injectable({ providedIn: 'root' })
export class ChatSessionService {
  private readonly http    = inject(HttpClient);
  private readonly auth    = inject(AuthService);
  private readonly apiUrl  = environment.apiUrl;

  private headers(): HttpHeaders {
    const token = this.auth.token();
    return token
      ? new HttpHeaders({ Authorization: `Bearer ${token}` })
      : new HttpHeaders();
  }

  async list(): Promise<ChatSessionSummary[]> {
    return firstValueFrom(
      this.http.get<ChatSessionSummary[]>(`${this.apiUrl}/api/chats`, { headers: this.headers() })
    );
  }

  async create(title: string, includeSharedDocs: boolean): Promise<ChatSessionSummary> {
    return firstValueFrom(
      this.http.post<ChatSessionSummary>(
        `${this.apiUrl}/api/chats`,
        { title, includeSharedDocs },
        { headers: this.headers() }
      )
    );
  }

  async get(id: string): Promise<ChatSessionDetail> {
    return firstValueFrom(
      this.http.get<ChatSessionDetail>(`${this.apiUrl}/api/chats/${id}`, { headers: this.headers() })
    );
  }

  async rename(id: string, title: string): Promise<void> {
    await firstValueFrom(
      this.http.patch(`${this.apiUrl}/api/chats/${id}`, { title }, { headers: this.headers() })
    );
  }

  async delete(id: string): Promise<void> {
    await firstValueFrom(
      this.http.delete(`${this.apiUrl}/api/chats/${id}`, { headers: this.headers() })
    );
  }

  async uploadDocument(sessionId: string, file: File): Promise<void> {
    const form = new FormData();
    form.append('file', file);
    await firstValueFrom(
      this.http.post(`${this.apiUrl}/api/chats/${sessionId}/documents`, form, { headers: this.headers() })
    );
  }

  async listDocuments(sessionId: string): Promise<string[]> {
    const res = await firstValueFrom(
      this.http.get<{ documents: string[] }>(`${this.apiUrl}/api/chats/${sessionId}/documents`, { headers: this.headers() })
    );
    return res.documents;
  }

  async deleteDocument(sessionId: string, name: string): Promise<void> {
    await firstValueFrom(
      this.http.delete(`${this.apiUrl}/api/chats/${sessionId}/documents/${encodeURIComponent(name)}`, { headers: this.headers() })
    );
  }
}
