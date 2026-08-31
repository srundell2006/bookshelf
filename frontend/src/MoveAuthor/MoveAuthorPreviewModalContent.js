import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import CheckInput from 'Components/Form/CheckInput';
import Button from 'Components/Link/Button';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { kinds } from 'Helpers/Props';
import translate from 'Utilities/String/translate';
import getSelectedIds from 'Utilities/Table/getSelectedIds';
import selectAll from 'Utilities/Table/selectAll';
import toggleSelected from 'Utilities/Table/toggleSelected';
import MoveAuthorPreviewRow from './MoveAuthorPreviewRow';
import styles from './MoveAuthorPreviewModalContent.css';

function getValue(allSelected, allUnselected) {
  if (allSelected) {
    return true;
  } else if (allUnselected) {
    return false;
  }

  return null;
}

class MoveAuthorPreviewModalContent extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      allSelected: false,
      allUnselected: false,
      lastToggled: null,
      selectedState: {}
    };
  }

  //
  // Control

  getSelectedIds = () => {
    return getSelectedIds(this.state.selectedState);
  };

  //
  // Listeners

  onSelectAllChange = ({ value }) => {
    this.setState(selectAll(this.state.selectedState, value));
  };

  onSelectedChange = ({ id, value, shiftKey = false }) => {
    this.setState((state) => {
      return toggleSelected(state, this.props.items, id, value, shiftKey);
    });
  };

  onMovePress = () => {
    this.props.onMovePress(this.getSelectedIds());
  };

  //
  // Render

  render() {
    const {
      isFetching,
      isPopulated,
      error,
      items,
      onModalClose
    } = this.props;

    const {
      allSelected,
      allUnselected,
      selectedState
    } = this.state;

    const selectAllValue = getValue(allSelected, allUnselected);

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          Preview Author Move
        </ModalHeader>

        <ModalBody>
          {
            isFetching &&
              <LoadingIndicator />
          }

          {
            !isFetching && error &&
              <div>
                {translate('ErrorLoadingPreviews')}
              </div>
          }

          {
            !isFetching && isPopulated && !items.length &&
              <div>
                Every author is already in the root folder that claims their letter.
              </div>
          }

          {
            !isFetching && isPopulated && !!items.length &&
              <div>
                <Alert>
                  <div>
                    Authors are filed by the first letter of their surname, into
                    whichever root folder claims that letter. Authors whose letter
                    is unclaimed are left where they are and are not listed here.
                  </div>
                </Alert>

                <div className={styles.previews}>
                  {
                    items.map((item) => {
                      return (
                        <MoveAuthorPreviewRow
                          key={item.authorId}
                          id={item.authorId}
                          authorName={item.authorName}
                          letter={item.letter}
                          existingPath={item.existingPath}
                          newPath={item.newPath}
                          files={item.files}
                          isSelected={selectedState[item.authorId]}
                          onSelectedChange={this.onSelectedChange}
                        />
                      );
                    })
                  }
                </div>
              </div>
          }
        </ModalBody>

        <ModalFooter>
          {
            isPopulated && !!items.length &&
              <CheckInput
                className={styles.selectAllInput}
                containerClassName={styles.selectAllInputContainer}
                name="selectAll"
                value={selectAllValue}
                onChange={this.onSelectAllChange}
              />
          }

          <Button
            onPress={onModalClose}
          >
            Cancel
          </Button>

          <Button
            kind={kinds.PRIMARY}
            onPress={this.onMovePress}
          >
            Move
          </Button>
        </ModalFooter>
      </ModalContent>
    );
  }
}

MoveAuthorPreviewModalContent.propTypes = {
  isFetching: PropTypes.bool.isRequired,
  isPopulated: PropTypes.bool.isRequired,
  error: PropTypes.object,
  items: PropTypes.arrayOf(PropTypes.object).isRequired,
  onMovePress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default MoveAuthorPreviewModalContent;
