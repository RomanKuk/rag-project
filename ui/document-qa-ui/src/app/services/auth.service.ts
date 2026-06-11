import { Injectable, signal, computed } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';

interface LoginResponse {
  token: string;
  role: string;
  tenantSlug: string;
  displayName: string;
  userId: string;
}

interface JwtPayload {
  sub: string;
  email: string;
  role: string;
  tenant_id: string;
  display_name: string;
  exp: number;
}

export interface CurrentUser {
  userId: string;
  email: string;
  role: 'Admin' | 'Owner' | 'Member';
  tenantSlug: string;
  displayName: string;
}

const TOKEN_KEY = 'dqa_token';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly _token  = signal<string | null>(localStorage.getItem(TOKEN_KEY));
  private readonly _user   = signal<CurrentUser | null>(this._decodeToken(localStorage.getItem(TOKEN_KEY)));

  readonly token       = this._token.asReadonly();
  readonly currentUser = this._user.asReadonly();
  readonly isLoggedIn  = computed(() => !!this._token());
  readonly role        = computed(() => this._user()?.role ?? null);

  constructor(private readonly http: HttpClient, private readonly router: Router) {}

  async login(email: string, password: string): Promise<void> {
    const res = await firstValueFrom(
      this.http.post<LoginResponse>(`${environment.apiUrl}/api/auth/login`, { email, password })
    );
    localStorage.setItem(TOKEN_KEY, res.token);
    this._token.set(res.token);
    this._user.set(this._decodeToken(res.token));
  }

  logout(): void {
    localStorage.removeItem(TOKEN_KEY);
    this._token.set(null);
    this._user.set(null);
    this.router.navigate(['/login']);
  }

  private _decodeToken(token: string | null): CurrentUser | null {
    if (!token) return null;
    try {
      const payload: JwtPayload = JSON.parse(atob(token.split('.')[1]));
      if (payload.exp * 1000 < Date.now()) {
        localStorage.removeItem(TOKEN_KEY);
        return null;
      }
      return {
        userId:      payload.sub,
        email:       payload.email,
        role:        payload.role as CurrentUser['role'],
        tenantSlug:  payload.tenant_id,
        displayName: payload.display_name,
      };
    } catch {
      return null;
    }
  }
}
