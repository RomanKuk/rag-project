import { Component, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../services/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss',
})
export class LoginComponent {
  email    = signal('');
  password = signal('');
  error    = signal('');
  loading  = signal(false);

  constructor(
    private readonly auth: AuthService,
    private readonly router: Router,
  ) {}

  async onSubmit(): Promise<void> {
    this.error.set('');
    this.loading.set(true);
    try {
      await this.auth.login(this.email(), this.password());
      const role = this.auth.role();
      if (role === 'Admin')  this.router.navigate(['/admin']);
      else if (role === 'Owner') this.router.navigate(['/owner']);
      else                       this.router.navigate(['/']);
    } catch (err: any) {
      this.error.set(err?.status === 0
        ? 'Cannot reach the server — is the backend running?'
        : 'Invalid email or password.');
    } finally {
      this.loading.set(false);
    }
  }
}
