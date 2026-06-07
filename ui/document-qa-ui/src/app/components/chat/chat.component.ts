import { Component, signal, ElementRef, viewChild, effect } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChatService } from '../../services/chat.service';
import { ChatMessage } from '../../models/chat.models';

@Component({
  selector: 'app-chat',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './chat.component.html',
  styleUrl: './chat.component.scss'
})
export class ChatComponent {
  messages = signal<ChatMessage[]>([]);
  question = signal('');
  isLoading = signal(false);

  private readonly scrollContainer = viewChild<ElementRef>('scrollContainer');

  constructor(private readonly chatService: ChatService) {
    // Auto-scroll when messages change
    effect(() => {
      this.messages();
      const el = this.scrollContainer()?.nativeElement as HTMLElement | undefined;
      if (el) {
        setTimeout(() => { el.scrollTop = el.scrollHeight; }, 0);
      }
    });
  }

  async sendMessage(): Promise<void> {
    const q = this.question().trim();
    if (!q || this.isLoading()) return;

    this.messages.update(msgs => [
      ...msgs,
      { role: 'user', content: q, sources: [] }
    ]);
    this.question.set('');
    this.isLoading.set(true);

    const assistantIndex = this.messages().length;
    this.messages.update(msgs => [
      ...msgs,
      { role: 'assistant', content: '', sources: [], isStreaming: true }
    ]);

    try {
      for await (const token of this.chatService.streamAnswer(q)) {
        this.messages.update(msgs => {
          const updated = [...msgs];
          updated[assistantIndex] = {
            ...updated[assistantIndex],
            content: updated[assistantIndex].content + token
          };
          return updated;
        });
      }
    } catch (err) {
      this.messages.update(msgs => {
        const updated = [...msgs];
        updated[assistantIndex] = {
          ...updated[assistantIndex],
          content: 'Error: could not reach the API. Make sure the backend is running.'
        };
        return updated;
      });
    } finally {
      this.messages.update(msgs => {
        const updated = [...msgs];
        updated[assistantIndex] = { ...updated[assistantIndex], isStreaming: false };
        return updated;
      });
      this.isLoading.set(false);
    }
  }

  onKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.sendMessage();
    }
  }
}
