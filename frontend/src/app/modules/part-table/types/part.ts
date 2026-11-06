export interface PartLink {
  label: string;
  url: string;
}

export type LifecycleStatus = 'active' | 'nrnd' | 'ltb' | 'eol';

export interface LifecycleOption {
  value: LifecycleStatus;
  label: string;
  badgeClass: string;
}

export const LIFECYCLE_OPTIONS: readonly LifecycleOption[] = [
  { value: 'active', label: 'Active', badgeClass: 'badge-success' },
  { value: 'nrnd', label: 'NRND', badgeClass: 'badge-warning' },
  { value: 'ltb', label: 'LTB', badgeClass: 'badge-warning' },
  { value: 'eol', label: 'EoL', badgeClass: 'badge-destructive' },
];

export function lifecycleLabel(status: string | null | undefined): string {
  return LIFECYCLE_OPTIONS.find(option => option.value === status)?.label ?? (status || 'Active');
}

export function lifecycleBadgeClass(status: string | null | undefined): string {
  return LIFECYCLE_OPTIONS.find(option => option.value === status)?.badgeClass ?? 'badge-muted';
}

export interface Part {
  id: number;
  uuid?: string;
  reviewState?: string;
  name: string;
  comment: string;
  datasheetId?: number;
  datasheetIds?: number[] | null;
  datasheet?: string;
  libraryId?: number;
  symbolId?: number;
  footprintId?: number;
  lifecycleStatus: LifecycleStatus;
  keywords?: string;
  value: string;
  properties?: string;
  alternatives?: number[] | null;
  links?: PartLink[] | null;
  imageHash?: string | null;
  updatedAt?: string;
  createdAt?: string;
}
