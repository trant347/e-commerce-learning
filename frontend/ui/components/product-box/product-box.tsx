import * as React from 'react';

import './product-box.css';

import {Card, Image, Button, Label, Icon} from 'semantic-ui-react';

type Props = {
    quantity: number,
    name: string,
    hourlyRateUsd: number,
    photo?: string,
    description?: string,
    openProduct?: Function,
    jobCategories?: string[],
    location?: string,
    rating?: number,
    age?: number
}

type State = {
    quantity: number
}

export default class ProductBox extends React.Component<Props, Readonly<State>> {

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

    getPictureSrc(imageName: string) {
        if (!imageName) {
            return 'https://via.placeholder.com/200x300?text=No+Photo';
        }
        if (imageName.indexOf("http") == 0) {
            return imageName;
        }
        return `products/image/${imageName}`;
    }

    openProduct() {
        this.props.openProduct ? this.props.openProduct(this.props.name) : null;
    }


    render() {
        return (

            <Card className="product-box">
                <Image onClick={this.openProduct.bind(this)} className="item-image" src={this.getPictureSrc(this.props.photo)} />
                <Card.Content>
                    <Card.Header>{this.props.name}</Card.Header>
                    <Card.Meta>
                        <Icon name="map marker alternate" /> {this.props.location}
                    </Card.Meta>
                    <Card.Description>
                        <div className="content-section">
                            {this.props.jobCategories?.map((cat, idx) => (
                                <Label key={idx} size="tiny" color="blue">{cat}</Label>
                            ))}
                        </div>
                        <div className="content-section">
                            <Icon name="star" color="yellow" /> {this.props.rating?.toFixed(1)}
                        </div>
                        <div className="content-section"> ${this.props.hourlyRateUsd}/hr </div>
                    </Card.Description>
                </Card.Content>              
            </Card>
        );

    }

}
