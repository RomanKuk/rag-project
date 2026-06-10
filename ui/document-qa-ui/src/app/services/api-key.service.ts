import { Injectable, signal } from '@angular/core';

const STORAGE_KEY = 'dqa_api_key';

@Injectable({ providedIn: 'root' })
export class ApiKeyService {
  readonly apiKey = signal<string>(localStorage.getItem(STORAGE_KEY) ?? '');

  set(key: string): void {
    this.apiKey.set(key);
    if (key) {
      localStorage.setItem(STORAGE_KEY, key);
    } else {
      localStorage.removeItem(STORAGE_KEY);
    }
  }

  get(): string {
    return this.apiKey();
  }
}
