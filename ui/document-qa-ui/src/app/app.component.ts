import { Component, inject, viewChild } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { DocumentUploadComponent } from './components/document-upload/document-upload.component';
import { DocumentListComponent } from './components/document-list/document-list.component';
import { ApiKeyService } from './services/api-key.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, FormsModule, DocumentUploadComponent, DocumentListComponent],
  templateUrl: './app.component.html',
})
export class AppComponent {
  private readonly apiKeyService = inject(ApiKeyService);
  private readonly docList = viewChild(DocumentListComponent);

  apiKey = this.apiKeyService.apiKey;

  onApiKeyChange(value: string): void {
    this.apiKeyService.set(value);
  }

  onUploaded(): void {
    this.docList()?.load();
  }
}
