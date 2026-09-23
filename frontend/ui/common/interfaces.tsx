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

export interface TaskMasterApplication {
    id: string;
    applicantUsername: string;
    name: string;
    age: number;
    location: string;
    description: string;
    hourlyRateUsd: number;
    photo?: string;
    jobCategories: string[];
    status: 'PENDING' | 'ACCEPTED' | 'DECLINED';
    submittedAt: string;
    declineReason?: string;
    createdTaskMasterId?: string;
    isViewedByAdmin: boolean;
}

export interface SubmitApplicationRequest {
    name: string;
    age: number;
    location: string;
    description: string;
    hourlyRateUsd: number;
    photo?: string | null;
    jobCategories: string[];
}

export interface CategoryMetadata {
    id: string;
    displayName: string;
    description: string;
}

export interface CreateCategoryRequest {
    id: string;
    displayName: string;
    description: string;
}

export interface UpdateCategoryRequest {
    displayName: string;
    description: string;
}