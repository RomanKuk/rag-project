import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { ApiKeyService } from '../services/api-key.service';

export const apiKeyInterceptor: HttpInterceptorFn = (req, next) => {
  const key = inject(ApiKeyService).get();
  if (!key) return next(req);
  return next(req.clone({ setHeaders: { 'X-API-Key': key } }));
};
