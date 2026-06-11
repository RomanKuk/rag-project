import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface TenantMetrics {
  tenantId: string;
  tenantName: string;
  requests: number;
  tokens: number;
  costUsd: number;
  cacheHitRate: number;
  avgLatencyMs: number;
  p95LatencyMs: number | null;
  userCount: number;
  documentCount: number;
  chunkCount: number;
  dailyTokenLimit?: number;
}

export interface OverallMetrics {
  totalTenants: number;
  totalUsers: number;
  totalRequests: number;
  totalTokens: number;
  totalCostUsd: number;
  cacheHitRate: number;
  topTenantsByCost: TenantMetrics[];
}

export interface DailyBucket {
  date: string;
  requests: number;
  tokens: number;
  costUsd: number;
  cacheHitRate: number;
}

export interface TenantSummary {
  id: string;
  name: string;
  slug: string;
  isActive: boolean;
  dailyTokenLimit: number;
  createdAt: string;
}

@Injectable({ providedIn: 'root' })
export class AdminService {
  constructor(private readonly http: HttpClient) {}

  getMetrics(): Observable<OverallMetrics> {
    return this.http.get<OverallMetrics>(`${environment.apiUrl}/api/admin/metrics`);
  }

  getTimeSeries(days = 30, tenantId?: string): Observable<DailyBucket[]> {
    const params: Record<string, string> = { days: days.toString() };
    if (tenantId) params['tenantId'] = tenantId;
    return this.http.get<DailyBucket[]>(`${environment.apiUrl}/api/admin/metrics/timeseries`, { params });
  }

  getTenants(): Observable<TenantSummary[]> {
    return this.http.get<TenantSummary[]>(`${environment.apiUrl}/api/admin/tenants`);
  }

  createTenant(
    name: string, ownerEmail: string, ownerPassword: string,
    ownerDisplayName?: string, dailyTokenLimit?: number
  ): Observable<{ tenantId: string; slug: string; ownerId: string }> {
    return this.http.post<{ tenantId: string; slug: string; ownerId: string }>(
      `${environment.apiUrl}/api/admin/tenants`,
      { name, ownerEmail, ownerPassword, ownerDisplayName, dailyTokenLimit: dailyTokenLimit ?? 0 }
    );
  }

  updateTenant(id: string, patch: { dailyTokenLimit?: number; isActive?: boolean }): Observable<TenantSummary> {
    return this.http.patch<TenantSummary>(`${environment.apiUrl}/api/admin/tenants/${id}`, patch);
  }
}
