import * as React from 'react';

import './product-box.css';

import {Card, Image, Button} from 'semantic-ui-react';

type Props = {
    quantity: number,
    name: string,
    priceUsd: number,
    picture?:string,
    description ?:string,
    openProduct ?: Function,
    authors?: string[]
}

type State = {
    quantity: number
}

export default class ProductBox extends React.Component<Props,  Readonly<State>> {

    constructor(props) {
        super(props);

        this.state = {
            quantity: props.quantity
        };

    }

    updateQuantity(event) {
        this.setState({
            quantity: event.target.value
        })
    }

    getPictureSrc(imageName:string) {
        if(imageName.indexOf("http") == 0) {
            return imageName;
        }
        return `products/image/${imageName}`;
    }

    openProduct() {
        this.props.openProduct ? this.props.openProduct(this.props.name) :null ;
    }


    render() {
        return (

            <Card className="product-box">
                <Image onClick={this.openProduct.bind(this)} className="item-image" src={ this.getPictureSrc(this.props.picture) }/>
                <Card.Content>
                    <Card.Header>{this.props.name}</Card.Header>
                    <Card.Description>
                        <div className="content-section"> {this.props.authors.join(" and ")} </div>                     
                        <div className="content-section"> C$ {this.props.priceUsd} </div>
                    </Card.Description>                  
                </Card.Content>
                <Card.Content extra>                   
                    <Button>
                        <Button.Content>Add to Cart</Button.Content>                        
                    </Button>
                </Card.Content>
            </Card>
        );

    }

}
