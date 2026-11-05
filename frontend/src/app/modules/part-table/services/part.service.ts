import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable, forkJoin, of, map, catchError } from 'rxjs';

import { appEnvironment } from '../../../app-environment';
import { Part } from '../types/part';
import { Sort } from '@angular/material/sort';

@Injectable({
  providedIn: 'root'
})
export class PartService {

  private apiUrl = appEnvironment.apiUrl;

  constructor(private http: HttpClient) { }

  // GET request to retrieve data
  getParts(sort: Sort): Observable<Part[]> {
    const params = new HttpParams()
      .set('_sort', sort.active)
      .set('_order', sort.direction);

    return forkJoin({
      parts: this.http.get<Part[]>(`${this.apiUrl}/part`, { params }),
      reviews: this.http.get<{ partUuid: string; state: string }[]>(`${this.apiUrl}/reviews/current`).pipe(catchError(() => of([])))
    }).pipe(map(({ parts, reviews }) => {
      const states = new Map(reviews.map(r => [r.partUuid, r.state]));
      return parts.map(part => ({ ...part, reviewState: states.get(part.uuid ?? '') ?? 'unknown' }));
    }));
  }

  getPart(id: number): Observable<Part> {
    return this.http.get<Part>(`${this.apiUrl}/part/${id}`);
  }

  // POST request to send data
  postData(data: any): Observable<any> {
    return this.http.post(`${this.apiUrl}/part`, data);
  }

  // PUT request to update data
  updateData(id: number, data: any): Observable<any> {
    return this.http.put(`${this.apiUrl}/part/${id}`, data);
  }

  // DELETE request to delete data
  deleteData(id: number): Observable<any> {
    return this.http.delete(`${this.apiUrl}/part/${id}`);
  }

  /** Photo already stored for a part. */
  partImageUrl(id: number): string {
    return `${this.apiUrl}/part/${id}/image`;
  }

  /** Photo stored by hash, before or after it is attached to a part. */
  imageContentUrl(hash: string): string {
    return `${this.apiUrl}/part/image/${hash}`;
  }

  uploadImage(file: File): Observable<{ hash: string }> {
    const body = new FormData();
    body.append('file', file, file.name);
    return this.http.post<{ hash: string }>(`${this.apiUrl}/part/image`, body);
  }
}
