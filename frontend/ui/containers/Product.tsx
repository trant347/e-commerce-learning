import * as React from 'react';
import ProductPage from "../components/product-page";
import {BookServices} from "../api/bookServices";

const errorMessageStyle = {
    display: "flex",
    paddingTop: "20px",
    justifyContent: "center"
}

export default class Product extends React.Component<any,any> {

    constructor(props) {
        super(props);

        this.state = {
            quantity: 0,
            book: {}
        }

    }

    async componentDidMount() {
        try{
            let bookData = await BookServices.getBookById(this.props.match.params.id);
            this.setState({
                book: bookData.data
            });
        } catch(e) {
            
            this.setState({
                book : {},
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
                    <ProductPage {...this.state.book}/>
                :
                    <div style={errorMessageStyle}>{this.state.error.message}</div>

        );
    }
}

