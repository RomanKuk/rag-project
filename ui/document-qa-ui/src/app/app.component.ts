import { Component, inject, signal, computed, effect } from '@angular/core';
import { RouterOutlet, RouterLink, Router, NavigationEnd } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { filter, map } from 'rxjs';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatMenuModule } from '@angular/material/menu';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from './services/auth.service';
import { ChatSessionService, ChatSessionSummary } from './services/chat-session.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, FormsModule, MatMenuModule, MatIconModule, MatButtonModule],
  templateUrl: './app.component.html',
})
export class AppComponent {
  readonly auth           = inject(AuthService);
  readonly sessionService = inject(ChatSessionService);
  private  readonly router = inject(Router);

  readonly sessions = this.sessionService.sessions;

  currentSessionId = signal<string | null>(null);

  // Inline rename state
  renamingId   = signal<string | null>(null);
  renameValue  = signal('');

  private readonly _url = toSignal(
    this.router.events.pipe(
      filter(e => e instanceof NavigationEnd),
      map(e => (e as NavigationEnd).urlAfterRedirects)
    ),
    { initialValue: this.router.url }
  );

  showShell = computed(() => {
    const url = this._url();
    return !url.startsWith('/login') && !url.startsWith('/admin') && !url.startsWith('/owner');
  });

  constructor() {
    effect(() => {
      if (this.auth.isLoggedIn()) {
        this.sessionService.reload();
      } else {
        this.sessionService.sessions.set([]);
        this.currentSessionId.set(null);
      }
    });
  }

  newChat(): void {
    // Navigate to the draft state — no session ID; chat auto-creates on first message
    this.currentSessionId.set(null);
    this.router.navigate(['']);
  }

  selectSession(id: string): void {
    this.currentSessionId.set(id);
    this.router.navigate([''], { queryParams: { session: id } });
  }

  startRename(session: ChatSessionSummary): void {
    this.renamingId.set(session.id);
    this.renameValue.set(session.title);
  }

  async commitRename(id: string): Promise<void> {
    const title = this.renameValue().trim();
    this.renamingId.set(null);
    if (title) {
      try {
        await this.sessionService.rename(id, title);
      } catch (e) {
        console.error('Failed to rename session', e);
      }
    }
  }

  async deleteSession(id: string, event: Event): Promise<void> {
    event.stopPropagation();
    if (!confirm('Delete this chat and its documents? This cannot be undone.')) return;
    try {
      await this.sessionService.delete(id);
      if (this.currentSessionId() === id) {
        this.currentSessionId.set(null);
        this.router.navigate(['']);
      }
    } catch (e) {
      console.error('Failed to delete session', e);
    }
  }
}
