import { useEffect, useState } from 'react';

import { TaskMasterServices } from '../../api/taskMasterServices';
import { CategoryMetadata } from '../../common/interfaces';

import './category-multi-select.css';

interface CategoryMultiSelectProps {
    selectedIds: string[];
    onChange: (selectedIds: string[]) => void;
    disabled?: boolean;
}

export default function CategoryMultiSelect({
    selectedIds,
    onChange,
    disabled = false,
}: CategoryMultiSelectProps) {
    const [categories, setCategories] = useState<CategoryMetadata[]>([]);
    const [loading, setLoading] = useState(true);
    const [loadError, setLoadError] = useState(false);
    const [loadAttempt, setLoadAttempt] = useState(0);

    useEffect(() => {
        let active = true;
        setLoading(true);
        setLoadError(false);

        TaskMasterServices.listCategoryMetadata()
            .then(loadedCategories => {
                if (active) {
                    setCategories(loadedCategories);
                }
            })
            .catch(() => {
                if (active) {
                    setCategories([]);
                    setLoadError(true);
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

    const toggleCategory = (categoryId: string) => {
        if (selectedIds.includes(categoryId)) {
            onChange(selectedIds.filter(id => id !== categoryId));
            return;
        }
        onChange([...selectedIds, categoryId]);
    };

    return (
        <fieldset className="category-multi-select" disabled={disabled || loading}>
            <legend>Categories *</legend>

            {loading && (
                <p className="category-catalog-status" role="status">
                    Loading categories...
                </p>
            )}

            {!loading && loadError && (
                <div className="category-catalog-status category-catalog-error" role="alert">
                    <span>Categories could not be loaded.</span>
                    <button type="button" onClick={() => setLoadAttempt(attempt => attempt + 1)}>
                        Retry
                    </button>
                </div>
            )}

            {!loading && !loadError && categories.length === 0 && (
                <p className="category-catalog-status" role="status">
                    No categories are currently available. An administrator must create one
                    before this form can be submitted.
                </p>
            )}

            {!loading && !loadError && categories.length > 0 && (
                <>
                    <p className="category-selection-help">
                        Select every service this TaskMaster can provide.
                    </p>
                    <div className="category-option-list">
                        {categories.map(category => (
                            <label className="category-option" key={category.id}>
                                <input
                                    type="checkbox"
                                    checked={selectedIds.includes(category.id)}
                                    onChange={() => toggleCategory(category.id)}
                                    disabled={disabled}
                                />
                                <span>
                                    <strong>{category.displayName}</strong>
                                    <small>{category.description}</small>
                                </span>
                            </label>
                        ))}
                    </div>
                </>
            )}
        </fieldset>
    );
}
