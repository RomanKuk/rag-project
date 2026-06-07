import { Component, signal } from '@angular/core';
import { DocumentService } from '../../services/document.service';

type UploadState = 'idle' | 'uploading' | 'success' | 'error';

@Component({
  selector: 'app-document-upload',
  standalone: true,
  templateUrl: './document-upload.component.html',
  styleUrl: './document-upload.component.scss'
})
export class DocumentUploadComponent {
  state = signal<UploadState>('idle');
  message = signal('');

  constructor(private readonly documentService: DocumentService) {}

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    this.state.set('uploading');
    this.message.set(`Uploading ${file.name}...`);

    this.documentService.upload(file).subscribe({
      next: (res) => {
        this.state.set('success');
        this.message.set(`"${res.fileName}" ingested successfully.`);
        input.value = '';
        setTimeout(() => { this.state.set('idle'); this.message.set(''); }, 3000);
      },
      error: () => {
        this.state.set('error');
        this.message.set('Upload failed. Check that the API is running.');
        setTimeout(() => { this.state.set('idle'); this.message.set(''); }, 4000);
      }
    });
  }
}
