import { ApplicationConfig, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideAnimationsAsync } from '@angular/platform-browser/animations/async';
import { routes } from './app.routes';
import { apiKeyInterceptor } from './interceptors/api-key.interceptor';
import { authInterceptor } from './interceptors/auth.interceptor';
import { unauthorizedInterceptor } from './interceptors/unauthorized.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZonelessChangeDetection(),
    provideAnimationsAsync(),
    provideRouter(routes),
    provideHttpClient(withInterceptors([
      authInterceptor,        // adds Bearer token when logged in
      apiKeyInterceptor,      // adds X-API-Key when not logged in (eval harness compat)
      unauthorizedInterceptor // redirects to /login on 401
    ]))
  ]
};
