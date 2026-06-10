import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class DocumentService {
  private readonly apiUrl = environment.apiUrl;

  constructor(private readonly http: HttpClient) {}

  upload(file: File): Observable<{ chunks: number; file: string; tenant: string }> {
    const formData = new FormData();
    formData.append('file', file);
    return this.http.post<{ chunks: number; file: string; tenant: string }>(
      `${this.apiUrl}/api/documents/upload`,
      formData
    );
  }

  list(): Observable<{ documents: string[]; tenant: string }> {
    return this.http.get<{ documents: string[]; tenant: string }>(
      `${this.apiUrl}/api/documents`
    );
  }

  delete(name: string): Observable<{ deleted: string; tenant: string }> {
    return this.http.delete<{ deleted: string; tenant: string }>(
      `${this.apiUrl}/api/documents/${encodeURIComponent(name)}`
    );
  }
}
