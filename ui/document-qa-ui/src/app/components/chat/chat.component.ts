import { Component, signal, ElementRef, viewChild, effect, inject, OnInit, DestroyRef } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ChatService, ChatStreamError } from '../../services/chat.service';
import { AuthService } from '../../services/auth.service';
import { ChatSessionService } from '../../services/chat-session.service';
import { ChatMessage, HistoryEntry } from '../../models/chat.models';
import { MarkdownPipe } from '../../pipes/markdown.pipe';

const TOOL_LABELS: Record<string, string> = {
  search_documents: 'Searching documents',
  summarize:        'Summarizing',
};

@Component({
  selector: 'app-chat',
  standalone: true,
  imports: [FormsModule, RouterModule, MarkdownPipe],
  templateUrl: './chat.component.html',
  styleUrl: './chat.component.scss'
})
export class ChatComponent implements OnInit {
  messages       = signal<ChatMessage[]>([]);
  question       = signal('');
  isLoading      = signal(false);
  historyLoading = signal(false);
  sessionId      = signal<string | null>(null);

  // Chat-scoped document panel
  showDocs     = signal(false);
  docs         = signal<string[]>([]);
  docUploading = signal(false);
  docError     = signal('');

  readonly auth           = inject(AuthService);
  private readonly chatService    = inject(ChatService);
  private readonly sessionService = inject(ChatSessionService);
  private readonly route          = inject(ActivatedRoute);
  private readonly destroyRef     = inject(DestroyRef);

  private readonly scrollContainer = viewChild<ElementRef>('scrollContainer');

  constructor() {
    effect(() => {
      this.messages();
      const el = this.scrollContainer()?.nativeElement as HTMLElement | undefined;
      if (el) setTimeout(() => { el.scrollTop = el.scrollHeight; }, 0);
    });
  }

  ngOnInit(): void {
    this.route.queryParamMap
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(params => {
        const sid = params.get('session');
        if (sid && sid !== this.sessionId()) {
          this.sessionId.set(sid);
          this.showDocs.set(false);
          this.loadHistory(sid);
          this.loadDocs(sid);
        } else if (!sid && this.sessionId()) {
          this.sessionId.set(null);
          this.messages.set([]);
          this.docs.set([]);
        }
      });
  }

  private async loadHistory(sessionId: string): Promise<void> {
    this.historyLoading.set(true);
    try {
      const session = await this.sessionService.get(sessionId);
      const msgs: ChatMessage[] = session.messages.map(m => ({
        role:      m.role,
        content:   m.content,
        sources:   m.sourcesJson ? JSON.parse(m.sourcesJson) : [],
        costUsd:   m.costUsd > 0 ? m.costUsd : undefined,
        messageId: m.role === 'assistant' ? m.id : undefined,
      }));
      this.messages.set(msgs);
    } catch {
      this.messages.set([]);
    } finally {
      this.historyLoading.set(false);
    }
  }

  // ── Chat-scoped documents ────────────────────────────────────────────────

  toggleDocs(): void {
    this.showDocs.update(v => !v);
  }

  private async loadDocs(sessionId: string): Promise<void> {
    try {
      this.docs.set(await this.sessionService.listDocuments(sessionId));
    } catch {
      this.docs.set([]);
    }
  }

  async onDocSelected(event: Event): Promise<void> {
    const input = event.target as HTMLInputElement;
    const file  = input.files?.[0];
    const sid   = this.sessionId();
    if (!file || !sid) return;

    this.docUploading.set(true);
    this.docError.set('');
    try {
      await this.sessionService.uploadDocument(sid, file);
      await this.loadDocs(sid);
    } catch {
      this.docError.set(`Upload failed for "${file.name}".`);
    } finally {
      this.docUploading.set(false);
      input.value = '';
    }
  }

  async deleteDoc(name: string): Promise<void> {
    const sid = this.sessionId();
    if (!sid) return;
    if (!confirm(`Remove "${name}" from this chat?`)) return;
    try {
      await this.sessionService.deleteDocument(sid, name);
      await this.loadDocs(sid);
    } catch {
      this.docError.set(`Could not delete "${name}".`);
    }
  }

  // ── Messaging ────────────────────────────────────────────────────────────

  async sendMessage(): Promise<void> {
    const q = this.question().trim();
    if (!q || this.isLoading()) return;

    // Auto-title the session after first user message
    const sid = this.sessionId();
    if (sid && this.messages().filter(m => m.role === 'user').length === 0) {
      const title = q.slice(0, 60);
      this.sessionService.rename(sid, title).catch(() => {});
    }

    this.messages.update(msgs => [...msgs, { role: 'user', content: q, sources: [] }]);
    this.question.set('');
    this.isLoading.set(true);

    const assistantIndex = this.messages().length;
    this.messages.update(msgs => [
      ...msgs,
      { role: 'assistant', content: '', sources: [], isStreaming: true },
    ]);

    const history: HistoryEntry[] = this.messages()
      .slice(0, assistantIndex)
      .filter(m => m.content)
      .map(m => ({ role: m.role, content: m.content }));

    const patch = (update: Partial<ChatMessage>) =>
      this.messages.update(msgs => {
        const updated = [...msgs];
        updated[assistantIndex] = { ...updated[assistantIndex], ...update };
        return updated;
      });

    try {
      for await (const event of this.chatService.streamAnswer(q, {
        agent:     true,
        sessionId: sid ?? undefined,
        history,
      })) {
        if (event.type === 'tool_call' && event.toolCall) {
          const label = event.toolCall.status === 'running'
            ? (TOOL_LABELS[event.toolCall.tool] ?? event.toolCall.tool) + '…'
            : null;
          patch({ activeToolCall: label ?? undefined });
        } else if (event.type === 'sources' && event.sources) {
          patch({ sources: event.sources, activeToolCall: undefined });
        } else if (event.type === 'token' && event.token) {
          patch({
            content: this.messages()[assistantIndex].content + event.token,
            activeToolCall: undefined,
          });
        } else if (event.type === 'no_context') {
          patch({
            content: 'No relevant documents found to answer your question.',
            activeToolCall: undefined,
          });
        } else if (event.type === 'done') {
          patch({
            costUsd:   event.cost_usd,
            cacheHit:  event.cache_hit,
            model:     event.usage?.model,
            messageId: event.message_id,
          });
        }
      }
    } catch (err) {
      patch({ content: this.describeStreamError(err), activeToolCall: undefined });
      if (err instanceof ChatStreamError && err.status === 401) {
        setTimeout(() => this.auth.logout(), 2500);
      }
    } finally {
      patch({ isStreaming: false, activeToolCall: undefined });
      this.isLoading.set(false);
    }
  }

  private describeStreamError(err: unknown): string {
    if (err instanceof ChatStreamError) {
      if (err.status === 429) {
        const limit = err.body?.limit
          ? ` The daily limit is ${err.body.limit.toLocaleString()} tokens.`
          : '';
        return `Daily token quota exceeded.${limit} Quota resets at midnight UTC.`;
      }
      if (err.status === 401) {
        return 'Your session has expired — redirecting to login…';
      }
      return err.body?.error ?? `The server rejected the request (HTTP ${err.status}).`;
    }
    return 'Error: could not reach the API. Make sure the backend is running.';
  }

  async sendFeedback(msgIndex: number, rating: 1 | -1): Promise<void> {
    const msg = this.messages()[msgIndex];
    if (!msg || msg.role !== 'assistant' || msg.feedbackSent) return;

    const previous = msg.feedback;
    this.messages.update(msgs => {
      const updated = [...msgs];
      updated[msgIndex] = { ...updated[msgIndex], feedback: rating };
      return updated;
    });

    try {
      await this.chatService.sendFeedback(msg.messageId ?? null, rating);
      this.messages.update(msgs => {
        const updated = [...msgs];
        updated[msgIndex] = { ...updated[msgIndex], feedbackSent: true };
        return updated;
      });
    } catch {
      // revert on failure so the user can retry
      this.messages.update(msgs => {
        const updated = [...msgs];
        updated[msgIndex] = { ...updated[msgIndex], feedback: previous };
        return updated;
      });
    }
  }

  clearHistory(): void {
    this.messages.set([]);
  }

  formatModel(model: string): string {
    return model.includes('/') ? model.split('/').pop()! : model;
  }

  onKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.sendMessage();
    }
  }
}
