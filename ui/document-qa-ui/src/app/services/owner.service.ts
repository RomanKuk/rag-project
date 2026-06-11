import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface UserSummary {
  id: string;
  email: string;
  displayName: string;
  role: string;
  isActive: boolean;
  createdAt: string;
}

@Injectable({ providedIn: 'root' })
export class OwnerService {
  constructor(private readonly http: HttpClient) {}

  listUsers(): Observable<UserSummary[]> {
    return this.http.get<UserSummary[]>(`${environment.apiUrl}/api/users`);
  }

  createUser(email: string, initialPassword: string, displayName?: string): Observable<{ userId: string; email: string; displayName: string }> {
    return this.http.post<{ userId: string; email: string; displayName: string }>(
      `${environment.apiUrl}/api/users`,
      { email, initialPassword, displayName }
    );
  }
}
