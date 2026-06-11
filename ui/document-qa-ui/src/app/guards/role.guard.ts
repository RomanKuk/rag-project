import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../services/auth.service';
import { CurrentUser } from '../services/auth.service';

export function roleGuard(role: CurrentUser['role']): CanActivateFn {
  return () => {
    const auth   = inject(AuthService);
    const router = inject(Router);
    if (auth.role() === role) return true;
    return router.createUrlTree(['/']);
  };
}
