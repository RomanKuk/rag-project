import { Component, inject, viewChild, computed } from '@angular/core';
import { RouterOutlet, RouterLink, Router, NavigationEnd } from '@angular/router';
import { filter, map } from 'rxjs';
import { toSignal } from '@angular/core/rxjs-interop';
import { DocumentUploadComponent } from './components/document-upload/document-upload.component';
import { DocumentListComponent } from './components/document-list/document-list.component';
import { AuthService } from './services/auth.service';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, DocumentUploadComponent, DocumentListComponent],
  templateUrl: './app.component.html',
})
export class AppComponent {
  readonly auth = inject(AuthService);
  private readonly router  = inject(Router);
  private readonly docList = viewChild(DocumentListComponent);

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

  onUploaded(): void {
    this.docList()?.load();
  }
}
