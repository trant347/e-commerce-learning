import * as React from 'react';

import { Dropdown } from 'semantic-ui-react';

import LookupServices from '../../api/lookupServices';

export default function(props) : JSX.Element {

        let { options = [], isAsync, groupId, value, onChange } = props;   

        const lookupServices = new LookupServices();

        let [selectedValue, setValue] = React.useState(value);

        let [lookupOptions, setOptions] = React.useState(options);

        React.useEffect(
                () => {
                        if(isAsync && groupId) {
                                lookupServices.getLookup(groupId)
                                        .then(
                                                lookups => {
                                                    setOptions(
                                                       lookups.map( (lookup) => {
                                                            return {
                                                                key: lookup.code,
                                                                text: lookup.label,
                                                                value: lookup.code
                                                            }    
                                                        })
                                                    );            
                                                }
                                        )
                        }    
                },
                [groupId]
        )

       
        return <Dropdown options={lookupOptions} selection clearable  value={selectedValue}
                                onChange={ 
                                        (e, { value }) => {
                                                let option = lookupOptions.find(op => op.key == value);     
                                                setValue(value);                                           
                                                if(option == undefined) {
                                                        onChange(option);
                                                } else {
                                                        onChange({
                                                                code: option.key,
                                                                label: option.text
                                                        });
                                                }
                                        }
                                }>                                        
                                </Dropdown>

}