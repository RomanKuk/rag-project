import { Component, OnInit, signal } from '@angular/core';
import { DocumentService } from '../../services/document.service';

@Component({
  selector: 'app-document-list',
  standalone: true,
  templateUrl: './document-list.component.html',
  styleUrl: './document-list.component.scss',
})
export class DocumentListComponent implements OnInit {
  documents = signal<string[]>([]);
  error     = signal('');
  deleting  = signal<string | null>(null);

  constructor(private readonly documentService: DocumentService) {}

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.documentService.list().subscribe({
      next:  r => this.documents.set(r.documents),
      error: () => this.error.set('Could not load documents'),
    });
  }

  delete(name: string): void {
    if (this.deleting()) return;
    this.deleting.set(name);
    this.documentService.delete(name).subscribe({
      next: () => {
        this.documents.update(docs => docs.filter(d => d !== name));
        this.deleting.set(null);
      },
      error: () => {
        this.error.set(`Failed to delete "${name}"`);
        this.deleting.set(null);
      },
    });
  }
}
