import { createAction } from 'redux-actions';
import { createThunk, handleThunks } from 'Store/thunks';
import createFetchHandler from './Creators/createFetchHandler';
import createHandleActions from './Creators/createHandleActions';

//
// Variables

export const section = 'moveAuthorPreview';

//
// State

export const defaultState = {
  isFetching: false,
  isPopulated: false,
  error: null,
  items: []
};

//
// Actions Types

export const FETCH_MOVE_AUTHOR_PREVIEW = 'moveAuthorPreview/fetchMoveAuthorPreview';
export const CLEAR_MOVE_AUTHOR_PREVIEW = 'moveAuthorPreview/clearMoveAuthorPreview';

//
// Action Creators

export const fetchMoveAuthorPreview = createThunk(FETCH_MOVE_AUTHOR_PREVIEW);
export const clearMoveAuthorPreview = createAction(CLEAR_MOVE_AUTHOR_PREVIEW);

//
// Action Handlers

export const actionHandlers = handleThunks({
  [FETCH_MOVE_AUTHOR_PREVIEW]: createFetchHandler('moveAuthorPreview', '/moveauthor')
});

//
// Reducers

export const reducers = createHandleActions({

  [CLEAR_MOVE_AUTHOR_PREVIEW]: (state) => {
    return Object.assign({}, state, defaultState);
  }

}, defaultState, section);
