import { Component, signal, ElementRef, viewChild, effect, inject, OnInit } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { ChatService } from '../../services/chat.service';
import { AuthService } from '../../services/auth.service';
import { ChatSessionService } from '../../services/chat-session.service';
import { ChatMessage, HistoryEntry } from '../../models/chat.models';

const TOOL_LABELS: Record<string, string> = {
  search_documents: 'Searching documents',
  summarize:        'Summarizing',
};

@Component({
  selector: 'app-chat',
  standalone: true,
  imports: [FormsModule, RouterModule],
  templateUrl: './chat.component.html',
  styleUrl: './chat.component.scss'
})
export class ChatComponent implements OnInit {
  messages    = signal<ChatMessage[]>([]);
  question    = signal('');
  isLoading   = signal(false);
  sessionId   = signal<string | null>(null);

  readonly auth           = inject(AuthService);
  private readonly chatService    = inject(ChatService);
  private readonly sessionService = inject(ChatSessionService);
  private readonly route          = inject(ActivatedRoute);

  private readonly scrollContainer = viewChild<ElementRef>('scrollContainer');

  constructor() {
    effect(() => {
      this.messages();
      const el = this.scrollContainer()?.nativeElement as HTMLElement | undefined;
      if (el) setTimeout(() => { el.scrollTop = el.scrollHeight; }, 0);
    });
  }

  ngOnInit(): void {
    this.route.queryParamMap.subscribe(params => {
      const sid = params.get('session');
      if (sid && sid !== this.sessionId()) {
        this.sessionId.set(sid);
        this.loadHistory(sid);
      } else if (!sid && this.sessionId()) {
        this.sessionId.set(null);
        this.messages.set([]);
      }
    });
  }

  private async loadHistory(sessionId: string): Promise<void> {
    try {
      const session = await this.sessionService.get(sessionId);
      const msgs: ChatMessage[] = session.messages.map(m => ({
        role:    m.role,
        content: m.content,
        sources: m.sourcesJson ? JSON.parse(m.sourcesJson) : [],
        costUsd: m.costUsd > 0 ? m.costUsd : undefined,
      }));
      this.messages.set(msgs);
    } catch {
      this.messages.set([]);
    }
  }

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
          this.messages.update(msgs => {
            const updated = [...msgs];
            updated[assistantIndex] = { ...updated[assistantIndex], activeToolCall: label ?? undefined };
            return updated;
          });
        } else if (event.type === 'sources' && event.sources) {
          this.messages.update(msgs => {
            const updated = [...msgs];
            updated[assistantIndex] = {
              ...updated[assistantIndex],
              sources: event.sources!,
              activeToolCall: undefined,
            };
            return updated;
          });
        } else if (event.type === 'token' && event.token) {
          this.messages.update(msgs => {
            const updated = [...msgs];
            updated[assistantIndex] = {
              ...updated[assistantIndex],
              content: updated[assistantIndex].content + event.token,
              activeToolCall: undefined,
            };
            return updated;
          });
        } else if (event.type === 'no_context') {
          this.messages.update(msgs => {
            const updated = [...msgs];
            updated[assistantIndex] = {
              ...updated[assistantIndex],
              content: 'No relevant documents found to answer your question.',
              activeToolCall: undefined,
            };
            return updated;
          });
        } else if (event.type === 'done') {
          this.messages.update(msgs => {
            const updated = [...msgs];
            updated[assistantIndex] = {
              ...updated[assistantIndex],
              costUsd:  event.cost_usd,
              cacheHit: event.cache_hit,
              model:    event.usage?.model,
            };
            return updated;
          });
        }
      }
    } catch {
      this.messages.update(msgs => {
        const updated = [...msgs];
        updated[assistantIndex] = {
          ...updated[assistantIndex],
          content: 'Error: could not reach the API. Make sure the backend is running.',
          activeToolCall: undefined,
        };
        return updated;
      });
    } finally {
      this.messages.update(msgs => {
        const updated = [...msgs];
        updated[assistantIndex] = { ...updated[assistantIndex], isStreaming: false, activeToolCall: undefined };
        return updated;
      });
      this.isLoading.set(false);
    }
  }

  async sendFeedback(msgIndex: number, rating: 1 | -1): Promise<void> {
    const msg = this.messages()[msgIndex];
    if (!msg || msg.role !== 'assistant') return;

    this.messages.update(msgs => {
      const updated = [...msgs];
      updated[msgIndex] = { ...updated[msgIndex], feedback: rating };
      return updated;
    });

    try {
      await this.chatService.sendFeedback(msg.messageId ?? null, rating);
    } catch {
      // non-critical — silently ignore
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
