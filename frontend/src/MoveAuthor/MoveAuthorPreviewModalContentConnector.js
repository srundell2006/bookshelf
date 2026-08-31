import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { createSelector } from 'reselect';
import * as commandNames from 'Commands/commandNames';
import { executeCommand } from 'Store/Actions/commandActions';
import { fetchMoveAuthorPreview } from 'Store/Actions/moveAuthorPreviewActions';
import MoveAuthorPreviewModalContent from './MoveAuthorPreviewModalContent';

function createMapStateToProps() {
  return createSelector(
    (state) => state.moveAuthorPreview,
    (moveAuthorPreview) => {
      return { ...moveAuthorPreview };
    }
  );
}

const mapDispatchToProps = {
  fetchMoveAuthorPreview,
  executeCommand
};

class MoveAuthorPreviewModalContentConnector extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      authorId,
      authorIds
    } = this.props;

    // One author from their detail page, or a selection from the library editor.
    // There is no whole-library mode - see MoveAuthorController.
    if (authorId != null) {
      this.props.fetchMoveAuthorPreview({ authorId });
    } else if (authorIds && authorIds.length) {
      this.props.fetchMoveAuthorPreview({ authorIds });
    }
  }

  //
  // Listeners

  onMovePress = (authorIds) => {
    this.props.executeCommand({
      name: commandNames.MOVE_AUTHOR_TO_LETTER,
      authorIds
    });

    this.props.onModalClose();
  };

  //
  // Render

  render() {
    return (
      <MoveAuthorPreviewModalContent
        {...this.props}
        onMovePress={this.onMovePress}
      />
    );
  }
}

MoveAuthorPreviewModalContentConnector.propTypes = {
  authorId: PropTypes.number,
  authorIds: PropTypes.arrayOf(PropTypes.number),
  fetchMoveAuthorPreview: PropTypes.func.isRequired,
  executeCommand: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default connect(createMapStateToProps, mapDispatchToProps)(MoveAuthorPreviewModalContentConnector);
