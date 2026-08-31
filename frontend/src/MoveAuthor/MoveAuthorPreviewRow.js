import PropTypes from 'prop-types';
import React, { Component } from 'react';
import CheckInput from 'Components/Form/CheckInput';
import Icon from 'Components/Icon';
import Label from 'Components/Label';
import { icons, kinds, sizes } from 'Helpers/Props';
import styles from './MoveAuthorPreviewRow.css';

const MAX_FILES_SHOWN = 5;

class MoveAuthorPreviewRow extends Component {

  //
  // Lifecycle

  componentDidMount() {
    const {
      id,
      onSelectedChange
    } = this.props;

    onSelectedChange({ id, value: true });
  }

  //
  // Listeners

  onSelectedChange = ({ value, shiftKey }) => {
    const {
      id,
      onSelectedChange
    } = this.props;

    onSelectedChange({ id, value, shiftKey });
  };

  //
  // Render

  render() {
    const {
      id,
      authorName,
      letter,
      existingPath,
      newPath,
      files,
      isSelected
    } = this.props;

    const shown = files.slice(0, MAX_FILES_SHOWN);
    const remaining = files.length - shown.length;

    return (
      <div className={styles.row}>
        <CheckInput
          containerClassName={styles.selectedContainer}
          name={id.toString()}
          value={isSelected}
          onChange={this.onSelectedChange}
        />

        <div className={styles.details}>
          <div className={styles.authorName}>
            {authorName}

            {
              letter &&
                <span className={styles.letter}>
                  <Label
                    kind={kinds.INFO}
                    size={sizes.SMALL}
                  >
                    {letter}
                  </Label>
                </span>
            }
          </div>

          <div>
            <Icon
              name={icons.SUBTRACT}
              kind={kinds.DANGER}
            />

            <span className={styles.path}>
              {existingPath}
            </span>
          </div>

          <div>
            <Icon
              name={icons.ADD}
              kind={kinds.SUCCESS}
            />

            <span className={styles.path}>
              {newPath}
            </span>
          </div>

          {
            !!files.length &&
              <div className={styles.files}>
                {
                  shown.map((file) => {
                    return (
                      <div key={file.bookFileId} className={styles.file}>
                        {file.newPath}
                      </div>
                    );
                  })
                }

                {
                  remaining > 0 &&
                    <div className={styles.file}>
                      {`and ${remaining} more file${remaining === 1 ? '' : 's'}`}
                    </div>
                }
              </div>
          }

          {
            !files.length &&
              <div className={styles.files}>
                <div className={styles.file}>
                  No book files - the folder is moved on its own.
                </div>
              </div>
          }
        </div>
      </div>
    );
  }
}

MoveAuthorPreviewRow.propTypes = {
  id: PropTypes.number.isRequired,
  authorName: PropTypes.string.isRequired,
  letter: PropTypes.string,
  existingPath: PropTypes.string,
  newPath: PropTypes.string.isRequired,
  files: PropTypes.arrayOf(PropTypes.object).isRequired,
  isSelected: PropTypes.bool,
  onSelectedChange: PropTypes.func.isRequired
};

MoveAuthorPreviewRow.defaultProps = {
  files: []
};

export default MoveAuthorPreviewRow;
