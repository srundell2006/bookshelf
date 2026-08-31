import { createAction } from 'redux-actions';
import { batchActions } from 'redux-batched-actions';
import { createThunk, handleThunks } from 'Store/thunks';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import { set, update } from './baseActions';
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

// Deliberately not createFetchHandler: that issues a GET with the payload as a query
// string, and a library-sized selection of author ids does not fit. Kestrel's request
// line caps at 8KB - 600 ids is already 9.6KB and comes back 414 URI Too Long, rejected
// before the API pipeline, which is why it never showed up in the server log. The ids go
// in a POST body instead.
export const actionHandlers = handleThunks({
  [FETCH_MOVE_AUTHOR_PREVIEW]: function(getState, payload, dispatch) {
    dispatch(set({ section, isFetching: true }));

    const { authorId, authorIds } = payload || {};
    const ids = authorId == null ? (authorIds || []) : [authorId];

    const { request } = createAjaxRequest({
      url: '/moveauthor',
      method: 'POST',
      dataType: 'json',
      data: JSON.stringify({ authorIds: ids })
    });

    request.done((data) => {
      dispatch(batchActions([
        update({ section, data }),

        set({
          section,
          isFetching: false,
          isPopulated: true,
          error: null
        })
      ]));
    });

    request.fail((xhr) => {
      dispatch(set({
        section,
        isFetching: false,
        isPopulated: false,
        error: xhr.aborted ? null : xhr
      }));
    });
  }
});

//
// Reducers

export const reducers = createHandleActions({

  [CLEAR_MOVE_AUTHOR_PREVIEW]: (state) => {
    return Object.assign({}, state, defaultState);
  }

}, defaultState, section);
