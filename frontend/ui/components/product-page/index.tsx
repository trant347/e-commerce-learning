import { useState } from 'react';
import { Link } from 'react-router-dom';

import './product-page.css';
import { Button, Label, Icon } from 'semantic-ui-react';

import styled from 'styled-components';

export interface ITaskMasterProps {
    id?: string,
    name: string,
    hourlyRateUsd: number,
    description?: string,
    photo?: string,
    jobCategories?: string[],
    location?: string,
    rating?: number,
    age?: number
}

const StyledContactSection = styled.div`       
    display: flex;
    gap: 10px;
    margin-top: 20px;
`;

const StyledRatingBadge = styled.div`
    display: flex;
    align-items: center;
    gap: 5px;
    font-size: 1.2em;
    color: #f5a623;
`;

export default function(props: ITaskMasterProps) {

    if (props == null || Object.keys(props).length == 0) {
        return <></>;
    }

    return (
        <div className="product-page">
            <div className="product-image">
                <img src={getPictureSrc(props.photo)} alt="Task Master Photo"></img>
            </div>

            <div className="product-details">
                <div className="product-name">
                    <h2> {props.name} </h2>
                    <StyledRatingBadge>
                        <Icon name="star" /> {props.rating?.toFixed(1)}
                    </StyledRatingBadge>
                </div>

                <div style={{ marginBottom: '10px' }}>
                    <Icon name="map marker alternate" /> {props.location}
                    {props.age && <span style={{ marginLeft: '15px' }}><Icon name="user" /> {props.age} years old</span>}
                </div>

                <div style={{ marginBottom: '15px' }}>
                    {props.jobCategories?.map((cat, idx) => (
                        <Label key={idx} color="blue" style={{ marginRight: '5px', marginBottom: '5px' }}>{cat}</Label>
                    ))}
                </div>

                <hr />

                <div className="product-description">
                    <strong> About me: </strong>
                    <div style={{ paddingTop: "10px", paddingBottom: "10px" }}> {props.description} </div>
                </div>

                <hr />

                <div style={{ fontSize: '1.3em', fontWeight: 'bold', marginTop: '15px' }}>
                    ${props.hourlyRateUsd}/hour
                </div>

                <StyledContactSection>
                    <Link to={`/booking/${props.id || ''}`}>
                        <Button primary size="large">
                            <Icon name="calendar" /> Book Now
                        </Button>
                    </Link>
                    <Button secondary size="large">
                        <Icon name="envelope" /> Contact
                    </Button>
                </StyledContactSection>

            </div>
        </div>
    )
}

function getPictureSrc(imageName: string): string {
    if (!imageName) {
        return 'https://via.placeholder.com/300x400?text=No+Photo';
    }
    if (imageName.indexOf("http") == 0) {
        return imageName;
    }
    return `products/image/${imageName}`;
}

