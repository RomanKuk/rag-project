import { Routes } from '@angular/router';
import { authGuard } from './guards/auth.guard';
import { roleGuard } from './guards/role.guard';

export const routes: Routes = [
  {
    path: 'login',
    loadComponent: () =>
      import('./components/login/login.component').then(m => m.LoginComponent),
  },
  {
    path: 'admin',
    canActivate: [authGuard, roleGuard('Admin')],
    loadComponent: () =>
      import('./components/admin-dashboard/admin-dashboard.component').then(m => m.AdminDashboardComponent),
  },
  {
    path: 'owner',
    canActivate: [authGuard, roleGuard('Owner')],
    loadComponent: () =>
      import('./components/owner-portal/owner-portal.component').then(m => m.OwnerPortalComponent),
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./components/chat/chat.component').then(m => m.ChatComponent),
  },
  { path: '**', redirectTo: '' },
];
