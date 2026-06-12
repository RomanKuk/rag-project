import {
  Component, signal, computed, ElementRef, viewChild,
  effect, inject, OnInit, DestroyRef
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
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
  imports: [
    FormsModule, RouterModule, MarkdownPipe,
    MatProgressSpinnerModule, MatSlideToggleModule,
    MatIconModule, MatButtonModule, MatTooltipModule,
  ],
  templateUrl: './chat.component.html',
  styleUrl: './chat.component.scss'
})
export class ChatComponent implements OnInit {
  messages       = signal<ChatMessage[]>([]);
  question       = signal('');
  isLoading      = signal(false);
  historyLoading = signal(false);
  sessionId      = signal<string | null>(null);

  // Draft state (before first message/upload creates the session)
  draftIncludeShared = signal(true);
  inputFocused       = signal(false);

  // Computed from the shared sessions signal so the toggle reflects the persisted value
  readonly includeShared = computed(() => {
    const sid = this.sessionId();
    if (!sid) return this.draftIncludeShared();
    return this.sessionService.sessions().find(s => s.id === sid)?.includeSharedDocs ?? true;
  });

  // Chat-scoped document panel
  showDocs     = signal(false);
  docs         = signal<string[]>([]);
  docUploading = signal(false);

  // Message-recall state
  private inputHistory: string[] = [];
  private historyCursor = -1;
  private draftStash    = '';

  readonly auth           = inject(AuthService);
  private readonly chatService    = inject(ChatService);
  private readonly sessionService = inject(ChatSessionService);
  private readonly route          = inject(ActivatedRoute);
  private readonly router         = inject(Router);
  private readonly snackBar       = inject(MatSnackBar);
  private readonly destroyRef     = inject(DestroyRef);

  private readonly scrollContainer = viewChild<ElementRef>('scrollContainer');
  private readonly promptInput     = viewChild<ElementRef>('promptInput');

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
          this.inputHistory = [];
          this.historyCursor = -1;
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
      this.inputHistory = msgs.filter(m => m.role === 'user').map(m => m.content);
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
    if (!file) return;

    this.docUploading.set(true);
    try {
      // Auto-create session if in draft mode
      let sid = this.sessionId();
      if (!sid) {
        const session = await this.sessionService.create('New chat', this.draftIncludeShared());
        sid = session.id;
        this.sessionId.set(sid);
        this.router.navigate([''], { queryParams: { session: sid }, replaceUrl: true });
      }

      await this.sessionService.uploadDocument(sid, file);
      await this.loadDocs(sid);
      this.snackBar.open(`"${file.name}" added to chat`, 'Dismiss', { duration: 3000 });
    } catch {
      this.snackBar.open(`Upload failed for "${file.name}"`, 'Dismiss', { duration: 4000 });
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
      this.snackBar.open(`Could not delete "${name}"`, 'Dismiss', { duration: 3000 });
    }
  }

  async toggleIncludeShared(checked: boolean): Promise<void> {
    const sid = this.sessionId();
    if (!sid) {
      this.draftIncludeShared.set(checked);
      return;
    }
    try {
      await this.sessionService.updateSession(sid, { includeSharedDocs: checked });
    } catch {
      this.snackBar.open('Could not update shared-docs setting', 'Dismiss', { duration: 3000 });
    }
  }

  // ── Messaging ────────────────────────────────────────────────────────────

  async sendMessage(): Promise<void> {
    const q = this.question().trim();
    if (!q || this.isLoading()) return;

    // Push to recall history before clearing
    this.inputHistory.push(q);
    this.historyCursor = -1;
    this.draftStash    = '';

    // Auto-create session if in draft mode; title = first message snippet
    let sid = this.sessionId();
    if (!sid) {
      const title = q.slice(0, 60);
      const session = await this.sessionService.create(title, this.draftIncludeShared());
      sid = session.id;
      this.sessionId.set(sid);
      this.router.navigate([''], { queryParams: { session: sid }, replaceUrl: true });
    } else {
      // Auto-title only if still on the default name
      const current = this.sessionService.sessions().find(s => s.id === sid);
      if (current?.title === 'New chat' && this.messages().filter(m => m.role === 'user').length === 0) {
        this.sessionService.rename(sid, q.slice(0, 60)).catch(() => {});
      }
    }

    this.messages.update(msgs => [...msgs, { role: 'user', content: q, sources: [] }]);
    this.question.set('');
    // Reset textarea height
    const ta = this.promptInput()?.nativeElement as HTMLTextAreaElement | undefined;
    if (ta) { ta.style.height = 'auto'; }
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
      const msg = this.describeStreamError(err);
      patch({ content: msg, activeToolCall: undefined });
      if (err instanceof ChatStreamError) {
        if (err.status === 401) {
          this.snackBar.open('Session expired — redirecting to login…', 'Dismiss', { duration: 3000 });
          setTimeout(() => this.auth.logout(), 2500);
        } else if (err.status === 429) {
          const limit = err.body?.limit ? ` (limit: ${err.body.limit.toLocaleString()} tokens/day)` : '';
          this.snackBar.open(`Daily token quota exceeded${limit}`, 'Dismiss', { duration: 6000 });
        }
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

  // ── Auto-grow textarea ───────────────────────────────────────────────────

  autoGrow(event: Event): void {
    const ta = event.target as HTMLTextAreaElement;
    this.question.set(ta.value);
    ta.style.height = 'auto';
    ta.style.height = Math.min(ta.scrollHeight, 200) + 'px';
  }

  // ── Copy code (event delegation from .messages container) ────────────────

  onMessagesClick(event: MouseEvent): void {
    const btn = (event.target as HTMLElement).closest('.copy-btn') as HTMLElement | null;
    if (!btn) return;
    const raw = btn.dataset['copy'] ?? '';
    const text = raw
      .replace(/&amp;/g, '&')
      .replace(/&lt;/g, '<')
      .replace(/&gt;/g, '>')
      .replace(/&quot;/g, '"');
    navigator.clipboard.writeText(text).then(() => {
      const label = btn.querySelector('.copy-label');
      if (label) {
        label.textContent = 'Copied!';
        setTimeout(() => { label.textContent = 'Copy'; }, 2000);
      }
    });
  }

  // ── Keyboard handling ────────────────────────────────────────────────────

  onKeyDown(event: KeyboardEvent): void {
    const ta = event.target as HTMLTextAreaElement;

    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.sendMessage();
      return;
    }

    if (event.key === 'ArrowUp' && ta.selectionStart === 0 && this.inputHistory.length > 0) {
      event.preventDefault();
      if (this.historyCursor === -1) {
        // First press — stash the current draft
        this.draftStash = this.question();
        this.historyCursor = this.inputHistory.length - 1;
      } else if (this.historyCursor > 0) {
        this.historyCursor--;
      }
      this.question.set(this.inputHistory[this.historyCursor]);
      // Move caret to start on next tick
      setTimeout(() => { ta.selectionStart = ta.selectionEnd = 0; }, 0);
      return;
    }

    if (event.key === 'ArrowDown' && ta.selectionStart === ta.value.length && this.historyCursor >= 0) {
      event.preventDefault();
      if (this.historyCursor < this.inputHistory.length - 1) {
        this.historyCursor++;
        this.question.set(this.inputHistory[this.historyCursor]);
      } else {
        // Past the end — restore the stash
        this.historyCursor = -1;
        this.question.set(this.draftStash);
      }
      setTimeout(() => { ta.selectionStart = ta.selectionEnd = ta.value.length; }, 0);
    }
  }
}
