import * as React from 'react';
import ProductBox from "../components/product-box/product-box";

import {Book} from "../common/interfaces";
import {BookServices} from "../api/bookServices";

export const BOOK_PER_PAGE = 6;

export interface BookState {
    books: Book[], 
    fetchingBooks: boolean, 
    pageNumber: number
}

export interface Action {
    type: string,
    payload: any
}

export default function Home({ history }) {


    const initialStates = {
        books: [],
        fetchingBooks: false,
        pageNumber: 0
    };

    const [booksState, dispatchBooksAction] = React.useReducer<BookState, Action>(booksReducer, initialStates);  

    React.useEffect(() => {
        try {
            const fetchBooks = async () => {
                let { data } = await BookServices.getBooksAtPage(booksState.pageNumber - 1, BOOK_PER_PAGE);
                dispatchBooksAction({ type: UPDATE_FETCHING_STATUS, payload: false});
                dispatchBooksAction({ type: UPDATE_BOOKS, payload: data });              
            }            
            if(booksState.fetchingBooks) {
                fetchBooks();      
            }   
        }catch (e) {
            throw Error("Failed to retrieve all books");
        }
    }, [booksState]);

    const bottomBoundaryRef = React.useRef(null);

    const scrollObserver = React.useCallback(node => {
            new IntersectionObserver(entries => {
                entries.forEach(en => {
                    if(en.isIntersecting) {
                        dispatchBooksAction({ type: INCREASE_PAGE_INDEX, payload: 1 });
                        dispatchBooksAction({ type: UPDATE_FETCHING_STATUS, payload: true});
                    }
                })
            }).observe(node);
        },
        [dispatchBooksAction],
    );

    React.useEffect(() => {
        if(bottomBoundaryRef.current) {
            scrollObserver(bottomBoundaryRef.current);
        }
    }, [bottomBoundaryRef, scrollObserver]);

    const openProduct = (book: Book) => {
        history.push(`/product/${book.id}`);
    }  

    

    return (
        <>
            <div className="App-content ui">
            {
                booksState.books.map(
                    (book,index) => <ProductBox key={index} {...book} quantity={1} openProduct={() => openProduct(book)}/>
                )
            }
            </div>
            <div id='page-bottom-boundary' style={{ border: '1px solid red' }} ref={bottomBoundaryRef}></div>
        </>
    );
    
}

export const UPDATE_BOOKS = "UPDATE_BOOKS";
export const UPDATE_FETCHING_STATUS = "UPDATE_FETCHING_STATUS";
export const INCREASE_PAGE_INDEX = "INCREASE_PAGE_INDEX";

export const booksReducer = ( state : BookState, action: Action ) => {
    switch (action.type) {
        case "UPDATE_BOOKS": {
            return {
                ...state,
                books: [
                    ...state.books,
                    ...action.payload
                ]
            }
        }
        case "UPDATE_FETCHING_STATUS": {
            return {
                ...state,
                fetchingBooks: action.payload
            }
        }
        case INCREASE_PAGE_INDEX: {
            return {
                ...state,
                pageNumber: state.pageNumber + action.payload
            }
        }
        default:
            return state;
    }
}