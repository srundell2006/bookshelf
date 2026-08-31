import PropTypes from 'prop-types';
import React from 'react';
import Modal from 'Components/Modal/Modal';
import { sizes } from 'Helpers/Props';
import MoveAuthorPreviewModalContentConnector from './MoveAuthorPreviewModalContentConnector';

function MoveAuthorPreviewModal(props) {
  const {
    isOpen,
    onModalClose,
    ...otherProps
  } = props;

  return (
    <Modal
      isOpen={isOpen}
      size={sizes.LARGE}
      onModalClose={onModalClose}
    >
      {
        isOpen &&
          <MoveAuthorPreviewModalContentConnector
            {...otherProps}
            onModalClose={onModalClose}
          />
      }
    </Modal>
  );
}

MoveAuthorPreviewModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default MoveAuthorPreviewModal;
