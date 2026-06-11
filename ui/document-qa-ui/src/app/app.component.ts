import { Component, inject, signal, computed, effect } from '@angular/core';
import { RouterOutlet, RouterLink, Router, NavigationEnd } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { filter, map } from 'rxjs';
import { toSignal } from '@angular/core/rxjs-interop';
import { AuthService } from './services/auth.service';
import { ChatSessionService } from './services/chat-session.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, FormsModule],
  templateUrl: './app.component.html',
})
export class AppComponent {
  readonly auth           = inject(AuthService);
  readonly sessionService = inject(ChatSessionService);
  private  readonly router = inject(Router);

  // Sidebar reads the service's shared signal — renames done elsewhere
  // (e.g. auto-title in the chat view) show up without a reload.
  readonly sessions = this.sessionService.sessions;

  currentSessionId = signal<string | null>(null);
  showNewDialog    = signal(false);
  newTitle         = signal('');
  newIncludeShared = signal(true);

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

  openNewChat(): void {
    this.newTitle.set('');
    this.newIncludeShared.set(true);
    this.showNewDialog.set(true);
  }

  async createChat(): Promise<void> {
    const title = this.newTitle().trim() || 'New chat';
    try {
      const session = await this.sessionService.create(title, this.newIncludeShared());
      this.currentSessionId.set(session.id);
      this.showNewDialog.set(false);
      this.router.navigate([''], { queryParams: { session: session.id } });
    } catch (e) {
      console.error('Failed to create chat', e);
    }
  }

  selectSession(id: string): void {
    this.currentSessionId.set(id);
    this.router.navigate([''], { queryParams: { session: id } });
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
