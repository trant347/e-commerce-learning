export interface Book {
    id: number,
    name: string,
    priceUsd: number,
    authors: string[],
    description?: string,
    picture?: string
}