export type PartStatusFilter = 'all' | 'active' | 'nrnd' | 'ltb' | 'eol';

export interface PartFilter {
  search: string;
  libraryId: number | 'all';
  manufacturerId: number | 'all';
  status: PartStatusFilter;
  reviewState?: string;
}

export const EMPTY_PART_FILTER: PartFilter = {
  search: '',
  libraryId: 'all',
  manufacturerId: 'all',
  status: 'all',
  reviewState: 'all',
};
