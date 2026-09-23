import * as React from 'react';
import { useParams } from 'react-router-dom';
import ProductPage from "../components/product-page";
import {TaskMasterServices} from "../api/taskMasterServices";
import { useCategoryCatalog } from '../hooks/useCategoryCatalog';
import { TaskMaster } from '../common/interfaces';

const errorMessageStyle: React.CSSProperties = {
    display: "flex",
    paddingTop: "20px",
    justifyContent: "center"
};

export default function Product() {
    const { id } = useParams<{ id: string }>();
    const { displayNamesById } = useCategoryCatalog();
    const [taskMaster, setTaskMaster] = React.useState<TaskMaster | null>(null);
    const [error, setError] = React.useState<{ message: string } | null>(null);

    React.useEffect(() => {
        if (!id) return;
        TaskMasterServices.getTaskMasterById(id)
            .then(tm => setTaskMaster(tm))
            .catch(() => {
                setTaskMaster(null);
                setError({ message: "You need to log in to view the content" });
            });
    }, [id]);

    if (error) {
        return <div style={errorMessageStyle}>{error.message}</div>;
    }

    if (!taskMaster) {
        return <></>;
    }

    return <ProductPage {...taskMaster} categoryDisplayNames={displayNamesById} />;
}
