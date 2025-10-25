import * as React from 'react';
import { useState } from 'react';

import './product-page.css';
import { Button } from 'semantic-ui-react';

import styled from 'styled-components';

export interface IProductProps {
    name: string,
    priceUsd: number,
    description?: string,
    quantity?: number,
    picture?: string,
    authors?: string[]
}

const StyledQuantityText = styled.div`       
    flex: 1; 
    display:flex;
    align-items: center;
`;

const StyledQuantityInput = styled.div`
    display:flex;
    flex-direction: row;
    align-items: center;    
    border-radius: 4px;
    border: 2px solid greenyellow;  
`

const StyledQuantitySection = styled.div `
    display: flex;
    width: 8em;
`;

export default function(props : IProductProps) {


    if(props == null || Object.keys(props).length == 0) {
        return <></>;
    }




    let [quantity, setQuantity] = useState(0);

    const increaseQuantity = () => {
        setQuantity(quantity+1);
    };

    const decreaseQuantity = () => {
        if(quantity > 0) {
            setQuantity(quantity - 1);
        }
        console.log(quantity);
    }

    return (
        <div className="product-page">
            <div className="product-image">
                <img src={getPictureSrc(props.picture)} alt="Image stored here"></img>
            </div>

            <div className="product-details">
                <div className="product-name">

                    <h2> {props.name} </h2>
                    <div> USD {props.priceUsd} </div>
                </div>

                <hr/>

                <div className="product-description">
                    <strong> Product description: </strong>
                    <div style={{paddingBottom:"10px"}}> {props.description} </div>
                    <div style={{paddingBottom:"10px"}}> By(author) {props.authors.join(" and ")} </div>
                </div>

                <hr/>

                <div className="quantity-form">
                    <StyledQuantitySection>
                        <StyledQuantityText> 
                            <label> Quantity </label>
                        </StyledQuantityText>
                        <StyledQuantityInput>
                            <input name="quantity" value={quantity} onChange={(e) => setQuantity(parseInt(e.target.value))}></input>
                            <div className="up-down-buttons">
                                <span onClick={increaseQuantity}><i className="caret up icon"></i></span>
                                <span onClick={decreaseQuantity}><i className="caret down icon"></i></span>
                            </div>
                        </StyledQuantityInput>
                    </StyledQuantitySection>                   
                    <Button> Add to cart </Button>
                </div>
                
            </div>
        </div>
    )
}

function getPictureSrc(imageName:string) : string{
    if(imageName.indexOf("http") == 0) {
        return imageName;
    }
    return `products/image/${imageName}`;
}

