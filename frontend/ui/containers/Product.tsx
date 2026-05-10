import * as React from 'react';
import ProductPage from "../components/product-page";
import {TaskMasterServices} from "../api/taskMasterServices";

const errorMessageStyle = {
    display: "flex",
    paddingTop: "20px",
    justifyContent: "center"
}

export default class Product extends React.Component<any,any> {

    constructor(props) {
        super(props);

        this.state = {
            taskMaster: {}
        }

    }

    async componentDidMount() {
        try{
            let taskMaster = await TaskMasterServices.getTaskMasterById(this.props.match.params.id);
            this.setState({
                taskMaster
            });
        } catch(e) {
            
            this.setState({
                taskMaster : {},
                error: {
                    message: "You need to log in to view the content"
                }
            })
        }
    }

    render() {
        return (
            !this.state.error 
                ?
                    <ProductPage {...this.state.taskMaster}/>
                :
                    <div style={errorMessageStyle}>{this.state.error.message}</div>

        );
    }
}

