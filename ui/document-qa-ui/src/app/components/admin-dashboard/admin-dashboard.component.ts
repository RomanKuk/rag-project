import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DecimalPipe } from '@angular/common';
import { BaseChartDirective } from 'ng2-charts';
import { Chart, ChartConfiguration, registerables } from 'chart.js';
import { AdminService, DailyBucket, OverallMetrics, TenantMetrics, TenantSummary } from '../../services/admin.service';
import { AuthService } from '../../services/auth.service';

Chart.register(...registerables);

@Component({
  selector: 'app-admin-dashboard',
  standalone: true,
  imports: [FormsModule, BaseChartDirective, DecimalPipe],
  templateUrl: './admin-dashboard.component.html',
  styleUrl: './admin-dashboard.component.scss',
})
export class AdminDashboardComponent implements OnInit {
  metrics   = signal<OverallMetrics | null>(null);
  tenants   = signal<TenantSummary[]>([]);
  loading   = signal(true);
  error     = signal('');

  // Tenant create form
  newTenantName        = signal('');
  newOwnerEmail        = signal('');
  newOwnerPassword     = signal('');
  newOwnerDisplayName  = signal('');
  createError          = signal('');
  createSuccess        = signal('');

  // Timeseries drill-down
  selectedTenantId = signal<string | undefined>(undefined);
  timeseriesDays   = signal(30);

  // Chart data
  barChartData = signal<ChartConfiguration<'bar'>['data'] | null>(null);
  lineChartData = signal<ChartConfiguration<'line'>['data'] | null>(null);

  readonly barChartOptions: ChartConfiguration<'bar'>['options'] = {
    responsive: true,
    plugins: { legend: { position: 'top' }, title: { display: true, text: 'Cost by Tenant (USD)' } },
    scales: { y: { beginAtZero: true } },
  };

  readonly lineChartOptions: ChartConfiguration<'line'>['options'] = {
    responsive: true,
    plugins: { legend: { position: 'top' }, title: { display: true, text: 'Daily Requests & Cost' } },
    scales: { y: { beginAtZero: true } },
  };

  constructor(
    private readonly adminSvc: AdminService,
    readonly auth: AuthService,
  ) {}

  ngOnInit(): void {
    this.loadDashboard();
  }

  loadDashboard(): void {
    this.loading.set(true);
    this.adminSvc.getMetrics().subscribe({
      next: m => {
        this.metrics.set(m);
        this._buildBarChart(m.topTenantsByCost);
        this.loading.set(false);
        this.loadTimeSeries();
        this.adminSvc.getTenants().subscribe(t => this.tenants.set(t));
      },
      error: () => { this.error.set('Failed to load metrics.'); this.loading.set(false); },
    });
  }

  loadTimeSeries(): void {
    this.adminSvc.getTimeSeries(this.timeseriesDays(), this.selectedTenantId()).subscribe({
      next: buckets => this._buildLineChart(buckets),
    });
  }

  createTenant(): void {
    this.createError.set('');
    this.createSuccess.set('');
    this.adminSvc.createTenant(
      this.newTenantName(), this.newOwnerEmail(), this.newOwnerPassword(), this.newOwnerDisplayName() || undefined
    ).subscribe({
      next: r => {
        this.createSuccess.set(`Tenant '${this.newTenantName()}' created (slug: ${r.slug})`);
        this.newTenantName.set('');
        this.newOwnerEmail.set('');
        this.newOwnerPassword.set('');
        this.newOwnerDisplayName.set('');
        this.loadDashboard();
      },
      error: err => this.createError.set(err.error?.error ?? 'Failed to create tenant.'),
    });
  }

  private _buildBarChart(tenants: TenantMetrics[]): void {
    this.barChartData.set({
      labels: tenants.map(t => t.tenantName),
      datasets: [
        {
          label: 'Cost (USD)',
          data: tenants.map(t => +t.costUsd.toFixed(4)),
          backgroundColor: 'rgba(99,102,241,0.7)',
          borderColor: '#6366f1',
          borderWidth: 1,
        },
        {
          label: 'Tokens (k)',
          data: tenants.map(t => +(t.tokens / 1000).toFixed(1)),
          backgroundColor: 'rgba(34,211,238,0.5)',
          borderColor: '#22d3ee',
          borderWidth: 1,
        },
      ],
    });
  }

  private _buildLineChart(buckets: DailyBucket[]): void {
    this.lineChartData.set({
      labels: buckets.map(b => b.date),
      datasets: [
        {
          label: 'Requests',
          data: buckets.map(b => b.requests),
          borderColor: '#6366f1',
          backgroundColor: 'rgba(99,102,241,0.15)',
          tension: 0.3,
          fill: true,
          yAxisID: 'y',
        },
        {
          label: 'Cost (USD)',
          data: buckets.map(b => +b.costUsd.toFixed(4)),
          borderColor: '#f59e0b',
          backgroundColor: 'rgba(245,158,11,0.1)',
          tension: 0.3,
          fill: false,
          yAxisID: 'y',
        },
      ],
    });
  }
}
