export interface Symbol {
  verified?: boolean;
  verificationState?: 'unverified' | 'verified' | 'stale' | 'needs_attention' | 'blocked';
  id: number;
  uuid: string;
  libraryFileId: number;
  name: string;
  reference?: string;
  inBom: boolean;
  onBoard: boolean;
  pinNumbersHidden: boolean;
  pinNamesHidden: boolean;
  pinNamesOffset: number;
  description: string;
  hash?: string;
  fileHash?: string;
  revisionNo?: number;
  headRevisionId?: number;
  updatedAt: string;
  createdAt: string;
  libraryFile?: object;
}
