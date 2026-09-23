import { useCallback, useEffect, useMemo, useState } from 'react';

import { TaskMasterServices } from '../api/taskMasterServices';
import { CategoryMetadata } from '../common/interfaces';

export interface CategoryCatalogState {
    categories: CategoryMetadata[];
    displayNamesById: Record<string, string>;
    loading: boolean;
    error: boolean;
    refresh: () => void;
}

export function useCategoryCatalog(): CategoryCatalogState {
    const [categories, setCategories] = useState<CategoryMetadata[]>([]);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState(false);
    const [loadAttempt, setLoadAttempt] = useState(0);

    useEffect(() => {
        let active = true;
        setLoading(true);
        setError(false);

        TaskMasterServices.listCategoryMetadata()
            .then(loadedCategories => {
                if (active) {
                    setCategories(loadedCategories);
                    setError(false);
                }
            })
            .catch(() => {
                if (active) {
                    setCategories([]);
                    setError(true);
                }
            })
            .finally(() => {
                if (active) {
                    setLoading(false);
                }
            });

        return () => {
            active = false;
        };
    }, [loadAttempt]);

    const displayNamesById = useMemo(
        () => categories.reduce<Record<string, string>>((displayNames, category) => {
            displayNames[category.id] = category.displayName;
            return displayNames;
        }, {}),
        [categories]
    );

    const refresh = useCallback(() => {
        setLoadAttempt(attempt => attempt + 1);
    }, []);

    return { categories, displayNamesById, loading, error, refresh };
}
