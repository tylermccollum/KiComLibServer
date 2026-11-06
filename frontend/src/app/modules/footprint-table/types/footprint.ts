export interface FootprintModel {
  position: number;
  model3dId: number;
  model3dUuid: string | null;
  directory: string | null;
  filename: string | null;
  offset: number[];
  scale: number[];
  rotate: number[];
  opacity: number | null;
  hide: boolean;
}

export interface Footprint {
  verified?: boolean;
  verificationState?: 'unverified' | 'verified' | 'stale' | 'needs_attention' | 'blocked';
  id: number;
  uuid: string;
  libraryFileId: number;
  name: string;
  type: number;
  boardOnly: boolean;
  excludeFromPosFiles?: boolean;
  /** Legacy / typo alias kept for older templates. */
  excludeFromPosFile?: boolean;
  excludeFromBom: boolean;
  height: number;
  layer: string;
  description: string;
  tags: string;
  property: string;
  model3dId: number | null;
  models?: FootprintModel[];
  hash?: string;
  fileHash?: string;
  revisionNo?: number;
  headRevisionId?: number;
  data?: string;
  updatedAt: string;
  createdAt: string;
}
