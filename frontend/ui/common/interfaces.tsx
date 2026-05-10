export interface TaskMaster {
    id: string,
    name: string,
    age: number,
    photo?: string,
    location: string,
    rating: number,
    jobCategories: string[],
    description?: string,
    hourlyRateUsd: number
}