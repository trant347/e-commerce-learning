import * as React from 'react';
import styled from 'styled-components';

export type DialogVariant = 'confirm' | 'alert';

interface DialogProps {
    title: string;
    message: string;
    variant?: DialogVariant;
    confirmLabel?: string;
    cancelLabel?: string;
    onConfirm?: () => void;
    onClose: () => void;
}

export default function Dialog({
    title,
    message,
    variant = 'alert',
    confirmLabel = 'Confirm',
    cancelLabel = 'Cancel',
    onConfirm,
    onClose,
}: DialogProps) {
    const isConfirm = variant === 'confirm';
    const titleColor = isConfirm ? '#d9534f' : '#333';

    return (
        <Overlay onClick={onClose}>
            <DialogBox onClick={(e) => e.stopPropagation()}>
                <Title style={{ color: titleColor }}>{title}</Title>
                <Message>{message}</Message>
                <Actions>
                    {isConfirm ? (
                        <>
                            <SecondaryButton onClick={onClose}>{cancelLabel}</SecondaryButton>
                            <DangerButton onClick={onConfirm}>{confirmLabel}</DangerButton>
                        </>
                    ) : (
                        <PrimaryButton onClick={onClose}>OK</PrimaryButton>
                    )}
                </Actions>
            </DialogBox>
        </Overlay>
    );
}

const Overlay = styled.div`
    position: fixed;
    top: 0;
    left: 0;
    right: 0;
    bottom: 0;
    background-color: rgba(0, 0, 0, 0.5);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1000;
`;

const DialogBox = styled.div`
    background: white;
    border-radius: 8px;
    padding: 2em;
    max-width: 420px;
    width: 90%;
    box-shadow: 0 4px 20px rgba(0, 0, 0, 0.25);
`;

const Title = styled.h3`
    margin: 0 0 0.8em;
    font-size: 1.3em;
`;

const Message = styled.p`
    margin: 0 0 1.5em;
    color: #555;
    line-height: 1.5;
`;

const Actions = styled.div`
    display: flex;
    justify-content: flex-end;
    gap: 0.8em;
`;

const buttonBase = `
    padding: 0.5em 1.2em;
    border-radius: 4px;
    cursor: pointer;
    font-size: 0.95em;
`;

const SecondaryButton = styled.button`
    ${buttonBase}
    background-color: #f0f0f0;
    color: #333;
    border: 1px solid #ccc;

    &:hover {
        background-color: #e0e0e0;
    }
`;

const DangerButton = styled.button`
    ${buttonBase}
    background-color: #d9534f;
    color: white;
    border: none;

    &:hover {
        background-color: #c9302c;
    }
`;

const PrimaryButton = styled.button`
    ${buttonBase}
    background-color: #0275d8;
    color: white;
    border: none;

    &:hover {
        background-color: #025aa5;
    }
`;
