import { Component, OnInit, signal, inject, DestroyRef } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { DatePipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { AuthService } from '../../services/auth.service';
import { OwnerService, UserSummary } from '../../services/owner.service';
import { DocumentService } from '../../services/document.service';

@Component({
  selector: 'app-owner-portal',
  standalone: true,
  imports: [FormsModule, RouterLink, DatePipe],
  templateUrl: './owner-portal.component.html',
  styleUrl: './owner-portal.component.scss',
})
export class OwnerPortalComponent implements OnInit {
  users        = signal<UserSummary[]>([]);
  documents    = signal<string[]>([]);
  loadingUsers = signal(true);
  loadingDocs  = signal(true);

  // Add user form
  newEmail       = signal('');
  newPassword    = signal('');
  newDisplayName = signal('');
  userError      = signal('');
  userSuccess    = signal('');

  // Upload
  uploadState   = signal<'idle'|'uploading'|'success'|'error'>('idle');
  uploadMessage = signal('');

  private readonly destroyRef = inject(DestroyRef);

  constructor(
    readonly auth: AuthService,
    private readonly ownerSvc: OwnerService,
    private readonly docSvc: DocumentService,
  ) {}

  ngOnInit(): void {
    this.loadUsers();
    this.loadDocs();
  }

  loadUsers(): void {
    this.loadingUsers.set(true);
    this.ownerSvc.listUsers().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: u => { this.users.set(u); this.loadingUsers.set(false); },
      error: () => this.loadingUsers.set(false),
    });
  }

  loadDocs(): void {
    this.loadingDocs.set(true);
    this.docSvc.list().pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: r => { this.documents.set(r.documents); this.loadingDocs.set(false); },
      error: () => this.loadingDocs.set(false),
    });
  }

  addUser(): void {
    this.userError.set('');
    this.userSuccess.set('');
    this.ownerSvc.createUser(
      this.newEmail(), this.newPassword(), this.newDisplayName() || undefined
    ).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: r => {
        this.userSuccess.set(`User ${r.email} added.`);
        this.newEmail.set('');
        this.newPassword.set('');
        this.newDisplayName.set('');
        this.loadUsers();
      },
      error: err => this.userError.set(err.error?.error ?? 'Failed to add user.'),
    });
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file  = input.files?.[0];
    if (!file) return;

    this.uploadState.set('uploading');
    this.uploadMessage.set(`Uploading ${file.name}…`);

    this.docSvc.upload(file).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({
      next: r => {
        this.uploadState.set('success');
        this.uploadMessage.set(`"${r.file}" ingested (${r.chunks} chunks, shared).`);
        input.value = '';
        this.loadDocs();
        setTimeout(() => { this.uploadState.set('idle'); this.uploadMessage.set(''); }, 3000);
      },
      error: () => {
        this.uploadState.set('error');
        this.uploadMessage.set('Upload failed.');
        setTimeout(() => { this.uploadState.set('idle'); this.uploadMessage.set(''); }, 3000);
      },
    });
  }

  deleteDoc(name: string): void {
    if (!confirm(`Delete "${name}" for the whole team? This cannot be undone.`)) return;
    this.docSvc.delete(name).pipe(takeUntilDestroyed(this.destroyRef)).subscribe({ next: () => this.loadDocs() });
  }
}
