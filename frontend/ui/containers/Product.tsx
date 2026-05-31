import * as React from 'react';
import { useParams } from 'react-router-dom';
import ProductPage from "../components/product-page";
import {TaskMasterServices} from "../api/taskMasterServices";

const errorMessageStyle: React.CSSProperties = {
    display: "flex",
    paddingTop: "20px",
    justifyContent: "center"
};

export default function Product() {
    const { id } = useParams<{ id: string }>();
    const [taskMaster, setTaskMaster] = React.useState<any>({});
    const [error, setError] = React.useState<{ message: string } | null>(null);

    React.useEffect(() => {
        if (!id) return;
        TaskMasterServices.getTaskMasterById(id)
            .then(tm => setTaskMaster(tm))
            .catch(() => {
                setTaskMaster({});
                setError({ message: "You need to log in to view the content" });
            });
    }, [id]);

    return !error
        ? <ProductPage {...taskMaster} />
        : <div style={errorMessageStyle}>{error.message}</div>;
}
