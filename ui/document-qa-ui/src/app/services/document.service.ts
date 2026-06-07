import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class DocumentService {
  private readonly apiUrl = environment.apiUrl;

  constructor(private readonly http: HttpClient) {}

  upload(file: File): Observable<{ message: string; fileName: string }> {
    const formData = new FormData();
    formData.append('file', file);
    return this.http.post<{ message: string; fileName: string }>(
      `${this.apiUrl}/api/documents/upload`,
      formData
    );
  }
}
