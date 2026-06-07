import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { DocumentUploadComponent } from './components/document-upload/document-upload.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, DocumentUploadComponent],
  templateUrl: './app.component.html'
})
export class AppComponent {}
