import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import { clearMoveAuthorPreview } from 'Store/Actions/moveAuthorPreviewActions';
import MoveAuthorPreviewModal from './MoveAuthorPreviewModal';

const mapDispatchToProps = {
  clearMoveAuthorPreview
};

class MoveAuthorPreviewModalConnector extends Component {

  //
  // Listeners

  onModalClose = () => {
    this.props.clearMoveAuthorPreview();
    this.props.onModalClose();
  };

  //
  // Render

  render() {
    return (
      <MoveAuthorPreviewModal
        {...this.props}
        onModalClose={this.onModalClose}
      />
    );
  }
}

MoveAuthorPreviewModalConnector.propTypes = {
  clearMoveAuthorPreview: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default connect(undefined, mapDispatchToProps)(MoveAuthorPreviewModalConnector);
